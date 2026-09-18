using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Systems;

public sealed class SystemSetupService(RomdDbContext db, IDatReplacementReview review,
    IPlatformHeaderResolver resolver, IUploadJobCreator uploads, ICurrentUser user) : ISystemSetupService
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];

    public async Task<IReadOnlyList<ManagedSystem>> ListAsync(CancellationToken ct)
    {
        var aliases = await db.PlatformAliases.Where(x => x.Type == PlatformAliasType.Name)
            .Select(x => new { x.PlatformId, x.Value }).ToListAsync(ct);
        // Bounded by the platform registry, not the number of catalog entries.
        var rows = await db.Platforms.OrderBy(x => x.Name).Select(p => new
        {
            Platform = p,
            HasData = db.DatFiles.Any(x => x.PlatformId == p.Id) || db.Titles.Any(x => x.PlatformId == p.Id)
                || db.DatSubscriptions.Any(x => x.PlatformId == p.Id),
            Catalogs = db.DatFiles.Count(x => x.PlatformId == p.Id && x.Lifecycle == "Active"),
            UsableCatalog = db.DatFiles.Any(d => d.PlatformId == p.Id && d.Lifecycle == "Active"
                && db.DatSources.Any(s => s.Id == d.DatSourceId
                    && db.CatalogSources.Any(c => c.Id == s.CatalogSourceId && c.Status == "Active"))),
            Owned = db.Titles.Count(x => x.PlatformId == p.Id && x.HasLocalPayload),
            Tracked = db.Titles.Count(x => x.PlatformId == p.Id && db.TrackedTitles.Any(t => t.TitleId == x.Id)),
            LastPhase = db.Jobs.Where(x => x.PlatformId == p.Id && (x is UploadJobEntity || x is ReplaceDatJobEntity))
                .OrderByDescending(x => x.CreatedAt).Select(x => x.Phase).FirstOrDefault(),
            Processing = db.Jobs.Any(x => x.PlatformId == p.Id && (x is UploadJobEntity || x is ReplaceDatJobEntity) && !Terminal.Contains(x.Phase))
        }).ToListAsync(ct);
        var manufacturers = await new Romd.Persistence.ReferenceData.SystemCompanyReader(db).ReadAsync(rows.Select(x => x.Platform.Id), ct);
        return rows.Select(x => new ManagedSystem(x.Platform.Id,
            x.Platform.CanonicalKey,
            x.Platform.Name, x.Platform.ShortName, Romd.Persistence.ReferenceData.SystemCompanyReader.Names(manufacturers.GetValueOrDefault(x.Platform.Id)),
            aliases.Where(a => a.PlatformId == x.Platform.Id).Select(a => a.Value).ToArray(),
            x.Platform.IsEnabled ?? x.HasData,
            x.UsableCatalog && x.Platform.CatalogRebuildState == CatalogRebuildState.Clean ? "Ready"
                : x.Platform.CatalogRebuildState == CatalogRebuildState.Failed ? "NeedsAttention"
                : x.Processing || x.Platform.CatalogRebuildState != CatalogRebuildState.Clean ? "Processing"
                : x.Catalogs > 0 || x.LastPhase is "Failed" or "CompletedWithErrors" or "Cancelled" ? "NeedsAttention" : "NeedsCatalog",
            x.Platform.CatalogRebuildState == CatalogRebuildState.Failed
                ? "Catalog processing needs attention. Your stored documents and ROMs are preserved. Open Jobs for details."
                : x.Catalogs > 0 && !x.UsableCatalog ? "No installed sources are active. Review their status in Catalog sources."
                : !x.UsableCatalog && x.LastPhase is "Failed" or "CompletedWithErrors" or "Cancelled"
                    ? "The source did not finish processing. Open Jobs for details and retry." : null,
            x.Catalogs, x.Owned, x.Tracked)).ToArray();
    }

    public async Task<ErrorOr<Success>> SetEnabledAsync(int platformId, bool enabled, CancellationToken ct)
    {
        var count = await db.Platforms.Where(x => x.Id == platformId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, enabled), ct);
        return count == 0 ? Error.NotFound("SystemSetup.NotFound", "This system no longer exists.") : Result.Success;
    }

    public async Task<ErrorOr<SystemDatPreview>> PreviewAsync(Stream document, CancellationToken ct)
    {
        var inspected = await review.InspectAsync(document, ct);
        if (inspected.IsError) return inspected.Errors;
        var suggested = await resolver.ResolvePlatformIdAsync(inspected.Value.Name, ct);
        var existing = await db.DatFiles.Where(x => x.Lifecycle == "Active" && x.Name == inspected.Value.Name)
            .Select(x => x.Id).ToListAsync(ct);
        return new SystemDatPreview(inspected.Value, suggested, existing);
    }

    public async Task<ErrorOr<UploadJobCreationResult>> ImportAsync(int platformId, Stream document, string hash, CancellationToken ct)
    {
        var inspected = await review.InspectAsync(document, ct);
        if (inspected.IsError) return inspected.Errors;
        if (inspected.Value.Preview.CandidateSha256 != hash)
            return Error.Conflict("SystemSetup.Stale", "The selected document changed. Review it again.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var enabled = await SetEnabledAsync(platformId, true, ct);
        if (enabled.IsError) return enabled.Errors;
        var documentHash = Romd.Domain.Hashing.Sha256.Parse(hash);
        if (await db.DatFiles.AnyAsync(d => db.Files.Any(f => f.Id == d.FileId && f.Sha256 == documentHash), ct))
            return Error.Conflict("SystemSetup.Unchanged", "This exact DAT document is already installed. Open its existing source; no new import is needed.");
        // This endpoint explicitly adds a source. Display-name overlap never
        // selects an existing source for replacement; updates use reviewed source IDs.
        document.Position = 0;
        var result = await uploads.CreateAsync(document, "reviewed-catalog.dat",
            new UploadJobOptions { PlatformId = platformId, CreatedByUserId = user.UserId }, ct);
        await transaction.CommitAsync(ct);
        return result;
    }
}
