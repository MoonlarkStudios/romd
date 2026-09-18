using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Artwork;

public sealed class ArtworkAcquisitionStore(RomdDbContext db, IArtworkCurationRepository curation, TimeProvider clock)
    : IArtworkAcquisitionStore
{
    public Task<string?> GetProviderGameIdAsync(int titleId, string providerId, CancellationToken ct) =>
        db.Set<TitleExternalIdEntity>().Where(x => x.TitleId == titleId && x.Provider == providerId)
            .Select(x => x.ExternalId).SingleOrDefaultAsync(ct);

    public Task<ArtworkEnrichmentSettingsDto> GetSettingsAsync(CancellationToken ct) =>
        db.Set<ArtworkEnrichmentSettingsEntity>().Select(x => new ArtworkEnrichmentSettingsDto(
            x.Revision, x.FillPosters, x.FillHeroes, x.FillLogos, Array.Empty<string>(), x.FillBackdrops, x.ReviewBackdrops)).SingleAsync(ct);

    public async Task<bool> UpdateSettingsAsync(UpdateArtworkEnrichmentSettingsRequest request, CancellationToken ct)
    {
        var row = await db.Set<ArtworkEnrichmentSettingsEntity>().AsTracking()
            .SingleOrDefaultAsync(item => item.Id == 1 && item.Revision == request.Revision, ct);
        if (row is null) return false;
        row.FillPosters = request.FillPosters;
        row.FillHeroes = request.FillHeroes;
        row.FillLogos = request.FillLogos;
        row.FillBackdrops = request.FillBackdrops;
        row.ReviewBackdrops = request.ReviewBackdrops;
        row.Revision = Guid.NewGuid();
        return await ConfigurationSave.TrySaveAsync(db, row, ct);
    }

    public async Task<ArtworkAcquisitionStateDto> GetOutcomesAsync(int titleId, CancellationToken ct)
    {
        var outcomes = await db.Set<ArtworkAcquisitionEntity>().Where(x => x.TitleId == titleId).OrderBy(x => x.Role)
            .Select(x => new ArtworkAcquisitionDto(x.Role.ToString(), x.Status, x.SourceId, x.UpdatedAt)).ToListAsync(ct);
        var active = await db.Set<EnrichmentJobEntity>().Where(x => x.TitleId == titleId &&
            (x.Phase == "Pending" || x.Phase == "Enriching")).OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        return new(outcomes, active);
    }

    public async Task RecordAsync(int titleId, ArtworkRole role, string status, string? sourceId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("An owned transaction is required.");
        // The title row serializes both outcome upserts and artwork publication.
        await db.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RetainWithoutCatalog, x => x.RetainWithoutCatalog), ct);
        var row = await db.Set<ArtworkAcquisitionEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.Role == role, ct);
        if (row is null) db.Add(row = new ArtworkAcquisitionEntity { TitleId = titleId, Role = role });
        row.Status = status;
        row.SourceId = sourceId;
        row.UpdatedAt = clock.GetUtcNow();
    }

    public async Task<bool> LockMissingAsync(int titleId, ArtworkRole role, long revision, CancellationToken ct)
    {
        var locked = await curation.LockSelectionAsync(titleId, role, revision, ct);
        if (locked.IsError) return false;
        var states = await curation.GetStatesAsync(titleId, ct);
        if (states.IsError) return false;
        var state = states.Value.Single(x => x.Role == role);
        if (state.Mode != ArtworkSelectionMode.Automatic || state.PendingJobId is not null) return false;
        if (await db.ArtworkAssets.AnyAsync(x => x.TitleId == titleId && x.Role == role && x.IsEligible, ct)) return false;
        return role != ArtworkRole.Poster || !await db.TitleMedia.AnyAsync(x => x.TitleId == titleId && x.Type == "Cover", ct);
    }
}
