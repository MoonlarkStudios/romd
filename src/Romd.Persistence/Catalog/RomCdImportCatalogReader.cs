using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Catalog;

/// <summary>One SQL statement captures bounded complete entries in one snapshot, without owning the caller's transaction.</summary>
public sealed class RomCdImportCatalogReader(RomdDbContext db) : IRomCdImportCatalogReader
{
    public async Task<IReadOnlyList<CatalogCdImportEntry>> FindAsync(IReadOnlyList<RomLogicalIdentity> tracks,
        IReadOnlyList<string> platformKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(platformKeys);
        if (tracks.Count is < 1 or > 99 || tracks.Any(track => track is null) || platformKeys.Count is < 1 or > 32 || platformKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A bounded complete track set and explicit registered platform scope are required.");
        var hashes = tracks.Select(track => (Sha1?)track.Sha1).Distinct().ToArray();
        var scope = platformKeys.Distinct(StringComparer.Ordinal).ToArray();
        var candidates = (from file in db.CatalogReleaseFiles
                          join release in db.CatalogReleases on file.CatalogReleaseId equals release.Id
                          join platform in db.Platforms on release.PlatformId equals platform.Id
                          where file.Sha1 != null && hashes.Contains(file.Sha1) && scope.Contains(platform.CanonicalKey!) && !release.HasTitleConflict
                          select release.Id).Distinct().Order().Take(257);
        // One overflow sentinel survives both bounds; the binder refuses instead of selecting a truncated catalog.
        var rows = await (from release in db.CatalogReleases
                          join platform in db.Platforms on release.PlatformId equals platform.Id
                          where candidates.Contains(release.Id)
                          from file in db.CatalogReleaseFiles.Where(file => file.CatalogReleaseId == release.Id).OrderBy(file => file.Id).Take(117)
                          select new
                          {
                              release.Id,
                              release.CatalogTitleId,
                              release.PlatformId,
                              PlatformKey = platform.CanonicalKey!,
                              release.Fingerprint,
                              FileId = file.Id,
                              FileFingerprint = file.FileFingerprint,
                              file.Name,
                              file.Size,
                              file.Sha1,
                              file.Md5,
                              file.Crc,
                              file.IsDisk
                          })
            .ToArrayAsync(ct);
        var result = rows.GroupBy(row => row.Id).Select(group =>
        {
            var entry = group.First();
            return new CatalogCdImportEntry(entry.Id, entry.CatalogTitleId, entry.PlatformId, entry.PlatformKey, entry.Fingerprint,
                group.Select(file => new CatalogCdFileRequirement(file.FileId, file.FileFingerprint, file.Name, file.Size,
                    file.Sha1, file.Md5, file.Crc, file.IsDisk)).ToArray());
        }).ToArray();
        return result;
    }
}
