using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;

namespace Romd.Persistence.Queries;

public sealed partial class TitleDetailReader
{
    private const int EmbeddedSourceLimit = 50;

    public async Task<TitleDetailData?> GetPublicDetailAsync(int titleId, CancellationToken ct = default)
    {
        var detail = await GetDetailCoreAsync(titleId, true, ct);
        if (detail is null)
            return null;
        var media = await GetMediaPageAsync(titleId, null, 50, ct);
        var releases = await GetReleasePageAsync(titleId, null, 50, ct);
        if (media.IsError || releases.IsError)
            return null;
        var representatives = ReleaseGroups(titleId).Select(group => group.Min(game => game.Id));
        var files = _context.DatRoms.Where(file => representatives.Contains(file.DatGameId));
        int total = await files.CountAsync(file => file.Status != "nodump" || file.Status == null, ct);
        int owned = await files.CountAsync(file => file.RomFileId != null, ct);
        return detail with
        {
            Media = media.Value.Page.Items,
            MediaNextCursor = media.Value.Page.NextCursor,
            MediaHasNextPage = media.Value.Page.HasNextPage,
            Releases = releases.Value.Page.Items,
            ReleasesNextCursor = releases.Value.Page.NextCursor,
            ReleasesHasNextPage = releases.Value.Page.HasNextPage,
            CompletionPercentOverride = total > 0 ? (double)owned / total * 100 : 0
        };
    }

    public async Task<ErrorOr<TitleReadPage<TitleMediaData>>> GetMediaPageAsync(int titleId, string? cursor, int limit, CancellationToken ct)
    {
        var revision = await RevisionAsync(titleId, ct);
        if (revision is null)
            return CatalogErrors.TitleNotFound();
        if (limit is < 1 or > 100)
            return InvalidLimit();
        string fingerprint = KeysetCursor.Fingerprint("admin.title.media", titleId, "id:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidCursor();
        int after = decoded.Value?.Id ?? 0;
        var rows = await (from media in _context.TitleMedia.AsNoTracking()
                          join file in _context.Files on media.FileId equals file.Id into files
                          from file in files.DefaultIfEmpty()
                          where media.TitleId == titleId && media.Id > after
                          orderby media.Id
                          select new TitleMediaData
                          {
                              Id = media.Id,
                              Type = media.Type,
                              FileId = media.FileId,
                              SourceId = media.SourceId,
                              Attribution = media.Attribution,
                              SourcePageUrl = media.SourcePageUrl,
                              IsPrimary = media.IsPrimary,
                              ContentType = media.ContentType,
                              CatalogReleaseId = media.CatalogReleaseId,
                              Region = media.Region,
                              Language = media.Language,
                              IsPreferred = media.IsPreferred,
                              ProviderAssetId = media.ProviderAssetId,
                              SizeBytes = file == null ? null : file.Size,
                              SizeOnDiskBytes = file == null ? null : file.SizeOnDisk,
                              IsCompressed = file == null ? null : file.IsCompressed
                          }).Take(limit + 1).ToListAsync(ct);
        return new TitleReadPage<TitleMediaData>(revision.Value, new PagedList<TitleMediaData>(rows.Take(limit).ToArray(), rows.Count > limit ? KeysetCursor.Encode(new IntegerPosition(rows[limit - 1].Id), fingerprint) : null, rows.Count > limit));
    }

    public async Task<ErrorOr<TitleReadPage<TitleReleaseData>>> GetReleasePageAsync(int titleId, string? cursor, int limit, CancellationToken ct)
    {
        var revision = await RevisionAsync(titleId, ct);
        if (revision is null)
            return CatalogErrors.TitleNotFound();
        if (limit is < 1 or > 100)
            return InvalidLimit();
        string fingerprint = KeysetCursor.Fingerprint("admin.title.releases", titleId, "representativeId:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidCursor();
        int after = decoded.Value?.Id ?? 0;
        // Select complete canonical groups before hydrating files and provenance. Unprojected games
        // retain one group each; only the exact asserting claim may collapse into a catalog release.
        var groups = await ReleaseGroups(titleId).Where(group => group.Min(game => game.Id) > after)
            .OrderBy(group => group.Min(game => game.Id)).Take(limit + 1)
            .Select(group => new ReleaseGroup(group.Key, group.Min(game => game.Id))).ToListAsync(ct);
        var pageGroups = groups.Take(limit).ToArray();
        if (pageGroups.Length == 0)
            return new TitleReadPage<TitleReleaseData>(revision.Value, new PagedList<TitleReleaseData>([], null, false));

        var ids = pageGroups.Select(group => group.RepresentativeId).ToArray();
        var games = await _context.DatGames.AsNoTracking().Where(game => ids.Contains(game.Id)).Include(game => game.Roms).ToDictionaryAsync(game => game.Id, ct);
        var sourcesByGroup = await ReadEmbeddedSourcesAsync(titleId, pageGroups.Select(group => group.Key).ToArray(), ct);
        List<TitleReleaseData> rows = [];
        foreach (var group in pageGroups)
        {
            var game = games[group.RepresentativeId];
            var sources = CreateSourcePage(sourcesByGroup[group.Key], EmbeddedSourceLimit,
                ReleaseSourcesFingerprint(titleId, group.RepresentativeId));
            rows.Add(new TitleReleaseData
            {
                Id = game.Id,
                CatalogReleaseId = group.Key > 0 ? group.Key : null,
                DatFileId = game.DatFileId,
                Name = game.Name,
                Description = game.Description,
                Year = game.Year,
                Manufacturer = game.Manufacturer,
                Region = game.Region,
                Language = game.Language,
                Revision = game.Revision,
                Files = game.Roms.Select(file => new TitleFileData
                {
                    Id = file.Id,
                    Name = file.Name,
                    Size = file.Size,
                    Sha1 = file.Sha1?.ToString(),
                    Md5 = file.Md5?.ToString(),
                    Crc = file.Crc?.ToString(),
                    Status = file.Status,
                    RomFileId = file.RomFileId
                }).ToArray(),
                Sources = sources.Items,
                SourcesNextCursor = sources.NextCursor,
                SourcesHasNextPage = sources.HasNextPage
            });
        }
        return new TitleReadPage<TitleReleaseData>(revision.Value, new PagedList<TitleReleaseData>(rows, groups.Count > limit ? KeysetCursor.Encode(new IntegerPosition(groups[limit - 1].RepresentativeId), fingerprint) : null, groups.Count > limit));
    }

    public async Task<ErrorOr<TitleReadPage<TitleReleaseSourceData>>> GetReleaseSourcesPageAsync(int titleId, string? releaseId, string? cursor, int limit, CancellationToken ct)
    {
        var revision = await RevisionAsync(titleId, ct);
        if (revision is null)
            return CatalogErrors.TitleNotFound();
        if (!IdCoder.TryDecode(releaseId, out int representativeId))
            return FieldErrors.Validation("Catalog.InvalidReleaseId", "Use a release id from this title's releases.", "releaseId");
        var group = await ReleaseGroups(titleId).Where(group => group.Min(game => game.Id) == representativeId)
            .Select(group => new ReleaseGroup(group.Key, group.Min(game => game.Id))).SingleOrDefaultAsync(ct);
        if (group is null)
            return Error.NotFound("Catalog.ReleaseNotFound", "The release was not found for this title.");
        if (limit is < 1 or > 100)
            return InvalidLimit();
        string fingerprint = ReleaseSourcesFingerprint(titleId, representativeId);
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidCursor();
        var sources = await ReadSourcesAsync(titleId, group, decoded.Value?.Id ?? 0, limit, ct);
        return new TitleReadPage<TitleReleaseSourceData>(revision.Value, CreateSourcePage(sources, limit, fingerprint));
    }

    private Task<TitleReleaseSourceData[]> ReadSourcesAsync(int titleId, ReleaseGroup selectedGroup, int position, int limit, CancellationToken ct) =>
        (from game in _context.DatGames.AsNoTracking()
         join dat in _context.DatFiles on game.DatFileId equals dat.Id
         where game.Id > position
             && _context.TitleSourceLinks.Any(link => link.TitleId == titleId && link.SourceEntryId == game.SourceEntryId)
             && (selectedGroup.Key < 0
                 ? game.Id == -selectedGroup.Key
                 : _context.CatalogReleaseSources.Any(source => source.SourceEntryId == game.SourceEntryId
                     && source.ProviderClaimKey == game.Id.ToString() && source.CatalogReleaseId == selectedGroup.Key))
         orderby game.Id
         select new TitleReleaseSourceData { DatGameId = game.Id, DatFileId = dat.Id, DatName = dat.Name, GameName = game.Name }).Take(limit + 1).ToArrayAsync(ct);

    private async Task<ILookup<int, TitleReleaseSourceData>> ReadEmbeddedSourcesAsync(int titleId, int[] groupKeys, CancellationToken ct)
    {
        int[] catalogReleaseIds = groupKeys.Where(key => key >= 0).ToArray();
        int[] unprojectedGameIds = groupKeys.Where(key => key < 0).Select(key => -key).ToArray();
        // A catalog release may have many claims outside this title. Materialize the intersection
        // of the title links and the page's release claims before expanding the exact DAT games.
        var rows = await _context.Database.SqlQuery<ReleaseSourceRow>($"""
            -- Admin title release provenance
            WITH title_claims AS MATERIALIZED (
                SELECT claim."CatalogReleaseId", claim."SourceEntryId", claim."ProviderClaimKey"
                FROM romd."TitleSourceLinks" link
                JOIN romd."CatalogReleaseSources" claim ON claim."SourceEntryId" = link."SourceEntryId"
                WHERE link."TitleId" = {titleId} AND claim."CatalogReleaseId" = ANY({catalogReleaseIds})
            ), ranked_sources AS (
                SELECT claim."CatalogReleaseId" AS "GroupKey", game."Id" AS "DatGameId", game."DatFileId", game."Name" AS "GameName",
                    row_number() OVER (PARTITION BY claim."CatalogReleaseId" ORDER BY game."Id") AS "Position"
                FROM title_claims claim
                JOIN romd."DatGames" game ON game."SourceEntryId" = claim."SourceEntryId"
                    AND game."Id"::text = claim."ProviderClaimKey"
            )
            SELECT source."GroupKey", source."DatGameId", dat."Id" AS "DatFileId", dat."Name" AS "DatName", source."GameName"
            FROM (
                SELECT "GroupKey", "DatGameId", "DatFileId", "GameName"
                FROM ranked_sources
                WHERE "Position" <= {EmbeddedSourceLimit + 1}
                UNION ALL
                SELECT -game."Id", game."Id", game."DatFileId", game."Name"
                FROM romd."DatGames" game
                JOIN romd."TitleSourceLinks" link ON link."SourceEntryId" = game."SourceEntryId"
                WHERE link."TitleId" = {titleId} AND game."Id" = ANY({unprojectedGameIds})
            ) source
            JOIN romd."DatFiles" dat ON dat."Id" = source."DatFileId"
            ORDER BY source."GroupKey", source."DatGameId"
            """).ToArrayAsync(ct);

        return rows.ToLookup(row => row.GroupKey, row => new TitleReleaseSourceData
        {
            DatGameId = row.DatGameId,
            DatFileId = row.DatFileId,
            DatName = row.DatName,
            GameName = row.GameName
        });
    }

    private static PagedList<TitleReleaseSourceData> CreateSourcePage(IEnumerable<TitleReleaseSourceData> rows, int limit, string fingerprint)
    {
        var items = rows.ToArray();
        if (items.Length <= limit)
            return new PagedList<TitleReleaseSourceData>(items, null, false);

        string cursor = KeysetCursor.Encode(new IntegerPosition(items[limit - 1].DatGameId), fingerprint);
        return new PagedList<TitleReleaseSourceData>(items[..limit], cursor, true);
    }

    private static string ReleaseSourcesFingerprint(int titleId, int representativeId) =>
        KeysetCursor.Fingerprint("admin.title.release-sources", titleId, representativeId, "datGameId:asc");

    private IQueryable<IGrouping<int, Romd.Persistence.Entities.DatGameEntity>> ReleaseGroups(int titleId) =>
        _context.DatGames.Where(game => _context.TitleSourceLinks.Any(link => link.TitleId == titleId && link.SourceEntryId == game.SourceEntryId))
            .GroupBy(game => _context.CatalogReleaseSources.Where(source => source.SourceEntryId == game.SourceEntryId && source.ProviderClaimKey == game.Id.ToString())
                .Select(source => (int?)source.CatalogReleaseId).FirstOrDefault() ?? -game.Id);
    private Task<Guid?> RevisionAsync(int titleId, CancellationToken ct) => _context.Titles.Where(title => title.Id == titleId).Select(title => (Guid?)title.Revision).SingleOrDefaultAsync(ct);
    private static Error InvalidCursor() => FieldErrors.Validation("Titles.InvalidCursor", "The cursor is invalid for this query.", "cursor");
    private static Error InvalidLimit() => FieldErrors.Validation("Titles.InvalidLimit", "Use a limit between 1 and 100.", "limit");
    private sealed record ReleaseGroup(int Key, int RepresentativeId);
    private sealed record ReleaseSourceRow(int GroupKey, int DatGameId, int DatFileId, string DatName, string GameName);
}
