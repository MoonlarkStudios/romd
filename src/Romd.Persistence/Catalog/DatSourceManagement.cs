using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed class DatSourceManagement(
    RomdDbContext db, IUnitOfWork unitOfWork, IDatRepository dats, ISourceLifecycle lifecycle,
    ITitleRepository titles, ICatalogProjectionService projection, ILibraryRepository libraries,
    IAdminEventOutbox outbox) : IDatSourceManagement
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
    private static bool Valid(string action) => action is "Delete" or "Disabled" or "Discontinued" or "Active";
    private static Error Missing() => Error.NotFound("SourceLifecycle.NotFound", "This source no longer exists. Refresh the system.");
    private static Error Busy() => Error.Conflict("SourceLifecycle.Busy", "A source check or import is running. Wait for it to finish, then review again.");

    public async Task<ErrorOr<SourceRemovalImpact>> PreviewAsync(int datId, string action, CancellationToken ct)
    {
        if (!Valid(action)) return Error.Validation("SourceLifecycle.Action", "Choose a valid source action.");
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await dats.AcquireMutationWriteLockAsync(datId, ct);
        var dat = await dats.GetByIdAsync(datId, ct);
        if (dat is null) return Missing();
        return await ReadImpactAsync(datId, dat.DatSourceId, dat.Name, action, ct);
    }

    public async Task<ErrorOr<Success>> ApplyAsync(int datId, string action, string reviewToken, CancellationToken ct)
    {
        if (!Valid(action)) return Error.Validation("SourceLifecycle.Action", "Choose a valid source action.");
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var dat = await dats.GetByIdAsync(datId, ct);
        if (dat is null) return Missing();
        // Initial imports are resolved by their exact document until the next check binds
        // the subscription. Fence those catalog identities too, before removing their DAT.
        foreach (var catalog in await RelatedSubscriptions(dat.DatSourceId).Select(s => s.CatalogId).Distinct().OrderBy(s => s).ToListAsync(ct))
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({catalog})) AS \"Value\"").SingleAsync(ct)) return Busy();
        // Subscription checks share this source fence; catalog topology has its existing fence below.
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(145, {dat.DatSourceId}) AS \"Value\"").SingleAsync(ct)) return Busy();
        await dats.AcquireMutationWriteLockAsync(datId, ct);
        dat = await dats.GetByIdAsync(datId, ct);
        if (dat is null) return Missing();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT t."Id" FROM romd."Titles" t
            WHERE EXISTS (SELECT 1 FROM romd."TitleSourceLinks" l
                JOIN romd."SourceEntries" e ON e."Id" = l."SourceEntryId"
                JOIN romd."DatSources" s ON s."CatalogSourceId" = e."CatalogSourceId"
                WHERE l."TitleId" = t."Id" AND s."Id" = {dat.DatSourceId})
            ORDER BY t."Id" FOR UPDATE OF t
            """, ct);
        var impact = await ReadImpactAsync(datId, dat.DatSourceId, dat.Name, action, ct);
        if (impact.Busy) return Busy();
        if (!string.Equals(reviewToken, impact.ReviewToken, StringComparison.Ordinal))
            return Error.Conflict("SourceLifecycle.Stale", "The source or its coverage changed. Review the updated impact before continuing.");
        var catalogSourceId = await dats.GetCatalogSourceIdAsync(dat.DatSourceId, ct);
        var affected = await lifecycle.GetLinkedTitleIdsAsync(catalogSourceId, ct);
        var platforms = await dats.GetRoutedPlatformIdsBySourceIdAsync(dat.DatSourceId, ct);
        await projection.RefreshCatalogSourcePayloadAsync(catalogSourceId, ct);
        await lifecycle.PreserveOwnedTitleIdentitiesAsync(catalogSourceId, ct);
        // Persist the exact-document association when disabling a just-imported source,
        // so scheduled checks observe its lifecycle immediately.
        await RelatedSubscriptions(dat.DatSourceId).Where(s => s.DatSourceId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DatSourceId, dat.DatSourceId), ct);
        if (action == "Delete")
        {
            var versions = await db.DatFiles.Where(d => d.DatSourceId == dat.DatSourceId).Select(d => d.Id).ToListAsync(ct);
            foreach (var version in versions) await dats.DeleteAsync(version, ct);
            await dats.DeleteSourceIfOrphanedAsync(dat.DatSourceId, ct);
            foreach (var id in await lifecycle.GetOrphanedTitleIdsAsync(affected, ct))
                await titles.DeleteOrRetainAsync(id, ct);
        }
        else await lifecycle.SetStatusAsync(catalogSourceId, Enum.Parse<CatalogSourceStatus>(action), ct);
        foreach (var platform in platforms)
        {
            await projection.MarkPlatformDirtyAsync(platform, ct, affected);
            await libraries.FlagForRematerializationByPlatformAsync(platform, ct);
        }
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }

    // Match the enrollment reader's exact-document result, never a display name.
    // An in-progress initial import is also related once its active DAT exists;
    // it must finish before source removal can proceed.
    private IQueryable<DatSubscriptionEntity> RelatedSubscriptions(int sourceId) => db.DatSubscriptions.Where(s =>
        s.DatSourceId == sourceId || (s.DatSourceId == null && s.JobId != null &&
            db.DatFiles.Any(d => d.DatSourceId == sourceId && d.FileId == s.CandidateFileId && d.PlatformId == s.PlatformId && d.Lifecycle == "Active") &&
            db.DatFiles.Count(d => d.FileId == s.CandidateFileId && d.PlatformId == s.PlatformId && d.Lifecycle == "Active") == 1));

    private async Task<SourceRemovalImpact> ReadImpactAsync(int datId, int sourceId, string name, string action, CancellationToken ct)
    {
        var source = await db.DatSources.Where(s => s.Id == sourceId).Select(s => s.CatalogSourceId).SingleAsync(ct);
        var versions = await db.DatFiles.Where(d => d.DatSourceId == sourceId).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.FileId, d.Lifecycle }).ToListAsync(ct);
        var status = await lifecycle.GetStatusAsync(source, ct);
        var busy = await db.Jobs.OfType<Romd.Persistence.Entities.ReplaceDatJobEntity>().AnyAsync(j =>
            db.DatFiles.Any(d => d.DatSourceId == sourceId && (d.Id == j.ExistingDatId || d.Id == j.NewDatId)) && !Terminal.Contains(j.Phase), ct)
            || await RelatedSubscriptions(sourceId).AnyAsync(s => db.Jobs.Any(j => j.Id == s.JobId && !Terminal.Contains(j.Phase)), ct)
            || versions.Any(v => v.Lifecycle == "PendingActivation");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add($"v1:{datId}:{sourceId}:{action}:{status}:{busy}:{name}");
        foreach (var subscription in await RelatedSubscriptions(sourceId).OrderBy(s => s.Id).Select(s => new { s.Id, s.JobId }).ToListAsync(ct))
            Add($"subscription:{subscription.Id}:{subscription.JobId}");
        foreach (var version in versions) Add($"{version.Id}:{version.FileId}:{version.Lifecycle}");
        var rows = db.Titles.Where(t => db.TitleSourceLinks.Any(l => l.TitleId == t.Id && db.SourceEntries.Any(e => e.Id == l.SourceEntryId && e.CatalogSourceId == source)))
            .OrderBy(t => t.Id).Select(t => new
            {
                t.Id,
                Covered = db.TitleSourceLinks.Any(l => l.TitleId == t.Id && db.EffectiveSourceEntries().Any(e => e.Id == l.SourceEntryId && e.CatalogSourceId != source
                    && (db.CatalogSources.Any(c => c.Id == e.CatalogSourceId && c.Kind != "Dat") || db.DatGames.Any(g => g.SourceEntryId == e.Id && db.DatFiles.Any(d => d.Id == g.DatFileId && d.Lifecycle == "Active"))))),
                Owned = db.TitleSourceLinks.Any(l => l.TitleId == t.Id && db.SourceEntries.Any(e => e.Id == l.SourceEntryId &&
                    (db.CatalogSources.Any(c => c.Id == e.CatalogSourceId && c.Kind != "Dat") ? e.HasLocalPayload :
                    db.DatGames.Any(g => g.SourceEntryId == e.Id && g.Roms.Any(r => r.RomFileId != null))))),
                Personal = t.RetainWithoutCatalog || db.TrackedTitles.Any(r => r.TitleId == t.Id) || t.FieldSourceOverridesJson != "{}" || t.FieldProvenanceJson != "{}"
                    || t.ScreenshotPrefsJson != "{}" || db.TitleExternalIds.Any(e => e.TitleId == t.Id && e.IsConfirmed)
                    || db.TitleMedia.Any(m => m.TitleId == t.Id && m.IsPrimary) || db.CollectionItems.Any(c => c.TitleId == t.Id)
            });
        var counts = new int[4];
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            var category = row.Covered ? 0 : row.Owned ? 1 : row.Personal ? 2 : 3;
            counts[category]++;
            Add($"{row.Id}:{category}");
        }
        return new(name, action, Convert.ToHexStringLower(hash.GetHashAndReset()), versions.Count,
            counts[0], counts[1], counts[2], counts[3], busy);
    }
}
