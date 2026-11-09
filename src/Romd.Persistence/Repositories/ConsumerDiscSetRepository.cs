using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Paths;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerDiscSetRepository(RomdDbContext db, IRomContentCatalog catalog, RomDeliveryPolicy deliveryPolicy,
    IDerivedDescriptorReader derivedDescriptors, IRomChdDeliveryAdmission? chdAdmission, IOriginalDiscLayoutReader? originalLayouts,
    DbContextOptions<RomdDbContext>? readOptions = null) : IConsumerDiscSetRepository
{
    public Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> ReadStatusAsync(ConsumerLibraryScope scope, int id, CancellationToken ct) =>
        ReadAsync(scope, id, false, ct);

    public Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> ReadForDownloadAsync(ConsumerLibraryScope scope, int id, CancellationToken ct) =>
        ReadAsync(scope, id, true, ct);

    private Task<ConsumerLibraryReadResult<ConsumerDiscSetPlan>> ReadAsync(ConsumerLibraryScope scope, int id, bool includeArchives, CancellationToken ct) =>
        new LiveConsumerLibraryQuery(db).ReadAsync<ConsumerDiscSetPlan>(scope, async (libraryId, token) =>
        {
            var rows = await db.MaterializedLibraryDiscSets.Where(x => x.LibraryId == libraryId && x.DiscSetId == id)
                .OrderBy(x => x.Id).Take(2).ToListAsync(token);
            if (rows.Count > 1)
                return new ConsumerLibraryProjectionResult<ConsumerDiscSetPlan>.ProjectionInconsistent();
            var built = rows.Count == 1 ? await BuildAsync(scope.UserId, libraryId, rows[0], token, includeArchives) : new BuildResult(null);
            return built.Inconsistent ? new ConsumerLibraryProjectionResult<ConsumerDiscSetPlan>.ProjectionInconsistent()
                : built.Plan is null ? new ConsumerLibraryProjectionResult<ConsumerDiscSetPlan>.ItemNotFound()
                : new ConsumerLibraryProjectionResult<ConsumerDiscSetPlan>.Found(built.Plan);
        }, ct);

    public Task<ConsumerLibraryReadResult<PagedList<ConsumerDiscSetPlan>>> ListPageAsync(
        ConsumerLibraryScope scope, int? titleId, string? cursor, int limit, CancellationToken ct) =>
        new LiveConsumerLibraryQuery(db).ReadAsync<PagedList<ConsumerDiscSetPlan>>(scope, async (libraryId, token) =>
        {
            string fingerprint = KeysetCursor.Fingerprint("consumer.disc-sets", scope.UserId, libraryId, titleId);
            var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
            if (decoded.IsError)
                return new ConsumerLibraryProjectionResult<PagedList<ConsumerDiscSetPlan>>.Rejected(decoded.FirstError);
            int after = decoded.Value?.Id ?? 0;
            var plans = new List<ConsumerDiscSetPlan>();
            bool exhausted = false;
            while (plans.Count <= limit && !exhausted)
            {
                var rows = await db.MaterializedLibraryDiscSets.Where(row => row.LibraryId == libraryId
                    && (titleId == null || row.TitleId == titleId) && row.DiscSetId > after)
                    .OrderBy(row => row.DiscSetId).Take(limit + 1).ToListAsync(token);
                exhausted = rows.Count < limit + 1;
                foreach (var row in rows)
                {
                    after = row.DiscSetId;
                    var built = await BuildAsync(scope.UserId, libraryId, row, token);
                    if (built.Plan is { } plan)
                        plans.Add(plan);
                    if (plans.Count > limit)
                        break;
                }
            }
            bool more = plans.Count > limit;
            var items = plans.Take(limit).ToArray();
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerDiscSetPlan>>.Found(new(items,
                more ? KeysetCursor.Encode(new IntegerPosition(items[^1].Id), fingerprint) : null, more));
        }, ct);

    private sealed record BuildResult(ConsumerDiscSetPlan? Plan, bool Inconsistent = false);

    private async Task<BuildResult> BuildAsync(Guid userId, int libraryId, MaterializedLibraryDiscSetEntity row, CancellationToken ct, bool includeArchives = false)
    {
        PublishedDiscSetDefinition? definition;
        try
        { definition = JsonSerializer.Deserialize<PublishedDiscSetDefinition>(row.DefinitionJson); }
        catch (JsonException) { return new(null, true); }
        if (definition is null || definition.Members.Count is < 1 or > 99
            || definition.Members.Select(x => x.Ordinal).Distinct().Count() != definition.Members.Count
            || definition.Members.Select(x => x.ReleaseId).Distinct().Count() != definition.Members.Count)
            return new(null, true);
        var discs = new List<ConsumerDiscSetDisc>();
        var releases = new ConsumerReleaseManifestRepository(db, deliveryPolicy, catalog, derivedDescriptors, chdAdmission);
        foreach (var member in definition.Members.OrderBy(x => x.Ordinal))
        {
            // Permission is metadata eligibility, independent of ownership/readiness. ShowMissingGames
            // can expose an unavailable disc honestly. No partial selection bypasses this all-member gate.
            var projections = await db.MaterializedLibraryReleases.Where(x => x.LibraryId == libraryId
                && x.CatalogReleaseId == member.ReleaseId).OrderBy(x => x.Id).Take(2).ToListAsync(ct);
            if (projections.Count != 1)
                return new(null, true);
            var projection = projections[0];
            if (projection.TitleId != row.TitleId || projection.PlatformId != row.PlatformId)
                return new(null, true);
            if (!projection.IsEligible || !projection.IsExposed || projection.IsBlocked)
                return new(null);
            var identity = await db.CatalogReleases.Where(x => x.Id == member.ReleaseId && x.CatalogTitleId == row.TitleId
                && x.PlatformId == row.PlatformId && x.Fingerprint == member.Fingerprint && !x.HasTitleConflict)
                .Select(x => new { x.Id, x.Name, x.Revision }).SingleOrDefaultAsync(ct);
            var system = await db.Platforms.Where(x => x.Id == row.PlatformId).Select(x => x.CanonicalKey).SingleOrDefaultAsync(ct);
            if (identity is null || system is null)
                return new(null, true);
            var result = projection.IsOwned ? await releases.GetManifestForLibraryAsync(userId, libraryId, member.ReleaseId, ct) : null;
            if (result is ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.ProjectionInconsistent)
                return new(null, true);
            var release = result is ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.Found found ? found.Value
                : new ConsumerReleaseManifest(userId, row.TitleId, row.PlatformId, system, identity.Id, identity.Name, identity.Revision,
                    false, new("disc", null, "files", 0), []);
            var paths = await db.CatalogReleaseFiles.Where(x => x.CatalogReleaseId == member.ReleaseId)
                .OrderBy(x => x.Id).Select(x => x.Name).ToListAsync(ct);
            var layoutIdentity = originalLayouts is null ? null : await originalLayouts.ReadIdentityAsync(row.PlatformId, member.ReleaseId, member.Fingerprint, ct);
            var attached = originalLayouts is null ? null : await originalLayouts.ResolveAsync(row.PlatformId, member.ReleaseId, member.Fingerprint, ct);
            bool stale = attached is null && originalLayouts is not null && await originalLayouts.HasStaleAsync(row.PlatformId, member.ReleaseId, member.Fingerprint, ct);
            string[] cues = paths.Where(x => x.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToArray();
            string? entry = attached?.EntryPoint ?? (cues.Length == 1 ? cues[0] : paths.Count == 1
                && new[] { ".iso", ".img" }.Contains(Path.GetExtension(paths[0]).ToLowerInvariant()) ? paths[0] : null);
            string readiness = !projection.IsOwned || !release.IsDeliveryComplete ? "missing-media"
                : stale ? "stale-layout"
                : PortablePathSet.Validate(attached?.Files.Select(x => x.Path).ToArray() ?? paths.ToArray()) is not null ? "unsupported-layout"
                : entry is null ? cues.Length > 1 || paths.Any(x => new[] { ".gdi", ".ccd", ".m3u" }.Contains(Path.GetExtension(x).ToLowerInvariant()))
                    ? "unsupported-layout" : "descriptor-required"
                : release.Items.Any(x => !x.IsAvailable) ? "missing-media" : "ready";
            var archive = includeArchives && readiness == "ready" && readOptions is not null && chdAdmission is not null
                ? await new ConsumerCdArchiveRepository(db, catalog, chdAdmission, deliveryPolicy, derivedDescriptors, readOptions).FindForReleaseAsync(release, ct, requireAdmission: true) : null;
            discs.Add(new(member.Ordinal, release, entry ?? string.Empty)
            { OriginalLayout = attached, LayoutIdentity = layoutIdentity, Readiness = readiness, CdArchive = archive });
        }
        return new(new(row.DiscSetId, row.Revision, definition.Name, definition.ExpectedDiscCount, discs));
    }
}
