using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Export;
using Romd.Domain.Libraries;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ReleaseDownloadRepository(RomdDbContext context) : IReleaseDownloadRepository
{
    public async Task<ReleaseDownloadPlan?> GetAsync(int releaseId, Guid userId, bool unrestricted, CancellationToken ct) =>
        (await GetManyAsync([releaseId], userId, unrestricted, ct)).GetValueOrDefault(releaseId);

    public async Task<IReadOnlyDictionary<int, ReleaseDownloadPlan>> GetManyAsync(IReadOnlyCollection<int> releaseIds, Guid userId,
        bool unrestricted, CancellationToken ct)
    {
        int[] ids = releaseIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, ReleaseDownloadPlan>();
        // The exact releases and their ownership are read in one statement. A user's library is
        // read from persistence, never inferred from an absent library claim.
        var rows = await (from release in context.CatalogReleases
                          where ids.Contains(release.Id) &&
                              context.Users.Any(user => user.Id == userId && !user.IsSuspended && !user.RequiresActivation &&
                                  (unrestricted || context.MaterializedLibraryReleases.Any(snapshot =>
                                      snapshot.LibraryId == user.LibraryId && snapshot.CatalogReleaseId == release.Id &&
                                      snapshot.TitleId == release.CatalogTitleId && snapshot.IsExposed && snapshot.IsOwned &&
                                      context.Libraries.Any(library => library.Id == snapshot.LibraryId &&
                                          library.ConfigurationState == LibraryConfigurationState.Valid.ToString() &&
                                          !(library.NeedsMaterialization && library.LastMaterializedAt == null)))))
                          join platform in context.Platforms on release.PlatformId equals platform.Id
                          join file in context.CatalogReleaseFiles on release.Id equals file.CatalogReleaseId
                          join rom in context.RomContent() on file.Sha1 equals (Romd.Domain.Hashing.Sha1?)rom.Sha1 into roms
                          from rom in roms.DefaultIfEmpty()
                          select new { ReleaseId = release.Id, release.Name, PlatformKey = platform.CanonicalKey, Path = file.Name, file.Status, FileId = (int?)rom.FileId, RomId = (int?)rom.RomId })
            .ToListAsync(ct);
        // Every member is reached through the release's platform, which resolves its media kind.
        return rows.GroupBy(row => row.ReleaseId).ToDictionary(release => release.Key, release => new ReleaseDownloadPlan(
            release.First().Name,
            release.Where(row => row.FileId.HasValue).Select(row => new ExportArchiveEntry(row.FileId!.Value, row.Path, row.RomId,
                PlatformKeys: [row.PlatformKey])).ToArray(),
            release.Count(row => !row.FileId.HasValue && !string.Equals(row.Status, "nodump", StringComparison.OrdinalIgnoreCase))));
    }
}
