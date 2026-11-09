using Microsoft.EntityFrameworkCore;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Artwork;

public sealed class ArtworkAcquisitionStore(RomdDbContext db, IArtworkCurationRepository curation, TimeProvider clock)
    : IArtworkAcquisitionStore
{
    public async Task<IReadOnlyList<string>> GetProviderOrderAsync(ArtworkRole role, CancellationToken ct) =>
        ArtworkAcquisitionPolicy.ProviderOrder(role, await db.ArtworkPreferences.Where(x => x.Role == role)
            .OrderBy(x => x.Priority).ThenBy(x => x.SourceId).Select(x => x.SourceId).ToListAsync(ct));

    public async Task<IReadOnlyList<string>> GetRejectedCandidatesAsync(int titleId, ArtworkRole role, string providerId, string gameId, CancellationToken ct) =>
        await db.ArtworkAssets.Where(x => x.TitleId == titleId && x.Role == role && x.SourceId == providerId &&
            x.ProviderGameId == gameId && x.IsRejected && x.ProviderAssetId != null)
            .Select(x => x.ProviderAssetId!).Distinct().ToListAsync(ct);

    public Task<Guid?> GetTitleRevisionAsync(int titleId, CancellationToken ct) =>
        db.Titles.Where(x => x.Id == titleId).Select(x => (Guid?)x.Revision).SingleOrDefaultAsync(ct);

    public Task<string?> GetProviderGameIdAsync(int titleId, string providerId, CancellationToken ct) =>
        db.Set<TitleExternalIdEntity>().Where(x => x.TitleId == titleId && x.Provider == providerId)
            .Select(x => x.ExternalId).SingleOrDefaultAsync(ct);

    public Task<ArtworkEnrichmentSettings> GetSettingsAsync(CancellationToken ct) =>
        db.Set<ArtworkEnrichmentSettingsEntity>().Select(x => new ArtworkEnrichmentSettings(
            x.Revision, x.FillPosters, x.FillHeroes, x.FillLogos, Array.Empty<string>(), x.FillBackdrops, x.ReviewBackdrops)).SingleAsync(ct);

    public async Task<ErrorOr<ArtworkEnrichmentSettings>> StageSettingsAsync(UpdateArtworkEnrichmentSettingsRequest request, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Settings writes require a caller-owned transaction.");
        var revision = await db.Set<ArtworkEnrichmentSettingsEntity>().Select(item => item.Revision).SingleAsync(ct);
        if (!ifMatch.IsSatisfiedBy(revision) || await db.Set<ArtworkEnrichmentSettingsEntity>()
                .Where(item => item.Id == 1 && item.Revision == revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Revision, revision), ct) != 1)
            return Error.Custom(412, "ArtworkEnrichment.PreconditionFailed", "Enrichment settings changed. Reload before saving.");
        var row = await db.Set<ArtworkEnrichmentSettingsEntity>().AsTracking()
            .SingleAsync(item => item.Id == 1, ct);
        await db.Entry(row).ReloadAsync(ct);
        row.FillPosters = request.FillPosters;
        row.FillHeroes = request.FillHeroes;
        row.FillLogos = request.FillLogos;
        row.FillBackdrops = request.FillBackdrops;
        row.ReviewBackdrops = request.ReviewBackdrops;
        row.Revision = Guid.NewGuid();
        return new ArtworkEnrichmentSettings(row.Revision, row.FillPosters, row.FillHeroes, row.FillLogos,
            [], row.FillBackdrops, row.ReviewBackdrops);
    }

    public async Task<ArtworkAcquisitionState> GetOutcomesAsync(int titleId, CancellationToken ct)
    {
        var outcomes = await db.Set<ArtworkAcquisitionEntity>().Where(x => x.TitleId == titleId).OrderBy(x => x.Role)
            .Select(x => new ArtworkAcquisition(x.Role.ToString(), x.Status, x.SourceId, x.UpdatedAt)).ToListAsync(ct);
        var active = await db.Set<EnrichmentJobEntity>().Where(x => x.TitleId == titleId &&
            (x.Phase == "Pending" || x.Phase == "Enriching")).OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        active ??= await db.Set<ArtworkCompletionItemEntity>().Where(x => x.TitleId == titleId && x.CompletedAt == null &&
            db.Set<ArtworkCompletionJobEntity>().Any(job => job.Id == x.JobId && (job.Phase == "Pending" || job.Phase == "Enriching")))
            .Select(x => (Guid?)x.JobId).FirstOrDefaultAsync(ct);
        return new(outcomes, active);
    }

    public async Task RecordAsync(int titleId, ArtworkRole role, string status, string? sourceId, CancellationToken ct, Guid? runId = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An owned transaction is required.");
        // The title row serializes both outcome upserts and artwork publication.
        await db.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RetainWithoutCatalog, x => x.RetainWithoutCatalog), ct);
        var row = await db.Set<ArtworkAcquisitionEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.Role == role, ct);
        if (row is null)
            db.Add(row = new ArtworkAcquisitionEntity { TitleId = titleId, Role = role });
        if (status == "AlreadyPresent" && row.Status == "Updated" && runId is not null && row.RunId == runId)
            return;
        if (runId is { } operation && Enum.TryParse<ArtworkCompletionOutcome>(status, out var outcome) &&
            await db.Set<ArtworkCompletionItemEntity>().AnyAsync(x => x.JobId == operation && x.TitleId == titleId, ct))
        {
            var result = await db.Set<ArtworkCompletionResultEntity>().AsTracking().SingleOrDefaultAsync(x => x.JobId == operation && x.TitleId == titleId && x.Role == role, ct);
            if (result is null)
                db.Add(new ArtworkCompletionResultEntity { JobId = operation, TitleId = titleId, Role = role, Outcome = outcome });
            else if (result.Outcome != ArtworkCompletionOutcome.Updated)
                result.Outcome = outcome;
        }
        row.RunId = runId;
        row.Status = status;
        row.SourceId = sourceId;
        row.UpdatedAt = clock.GetUtcNow();
    }

    public async Task<bool> LockMissingAsync(int titleId, ArtworkRole role, long revision, CancellationToken ct)
    {
        var locked = await curation.LockSelectionAsync(titleId, role,
            RevisionPrecondition.For(ArtworkValidators.Role(revision)), ct);
        if (locked.IsError)
            return false;
        var states = await curation.GetStatesAsync(titleId, ct);
        if (states.IsError)
            return false;
        var state = states.Value.Single(x => x.Role == role);
        if (state.Mode != ArtworkSelectionMode.Automatic || state.PendingJobId is not null)
            return false;
        if (await db.ArtworkAssets.AnyAsync(x => x.TitleId == titleId && x.Role == role && x.IsEligible && !x.IsRejected, ct))
            return false;
        return true;
    }
}
