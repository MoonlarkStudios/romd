using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed partial class DatSourceManagement(
    RomdDbContext db, IUnitOfWork unitOfWork, IDatRepository dats, ISourceLifecycle lifecycle)
    : IDatSourceManagement, IDatSourceLifecycleStore
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
    public async Task<ErrorOr<SourceRemovalImpact>> PreviewAsync(int datId, string action, CancellationToken ct)
    {
        if (!DatSourceLifecycleIntent.IsValid(action))
            return DatSourceLifecycleIntent.InvalidAction();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await dats.AcquireMutationWriteLockAsync(datId, ct);
        var dat = await dats.GetByIdAsync(datId, ct);
        if (dat is null)
            return DatSourceLifecycleIntent.Missing();
        return await ReadImpactAsync(datId, dat.DatSourceId, dat.Name, action, ct);
    }

    // Match the enrollment reader's exact-document result, never a display name.
    // An in-progress initial import is also related once its active DAT exists;
    // it must finish before source removal can proceed.
    private IQueryable<DatSubscriptionEntity> RelatedSubscriptions(int sourceId) => db.DatSubscriptions.Where(s =>
        s.DatSourceId == sourceId || (s.DatSourceId == null && s.JobId != null &&
            db.DatFiles.Any(d => d.DatSourceId == sourceId && d.FileId == s.CandidateFileId && d.PlatformId == s.PlatformId && d.Lifecycle == "Active") &&
            db.DatFiles.Count(d => d.FileId == s.CandidateFileId && d.PlatformId == s.PlatformId && d.Lifecycle == "Active") == 1));

    public async Task<SourceRemovalImpact> ReadImpactAsync(int datId, int sourceId, string name, string action, CancellationToken ct)
    {
        RequireMutationTransaction();
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
        foreach (var version in versions)
            Add($"{version.Id}:{version.FileId}:{version.Lifecycle}");
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
