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
        var sourcesByGroup = await ReadSourcesAsync(titleId, pageGroups.Select(group => group.Key).ToArray(), 0, EmbeddedSourceLimit, ct);
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
        var sourcesByGroup = await ReadSourcesAsync(titleId, [group.Key], decoded.Value?.Id ?? 0, limit, ct);
        return new TitleReadPage<TitleReleaseSourceData>(revision.Value, CreateSourcePage(sourcesByGroup[group.Key], limit, fingerprint));
    }

    private async Task<ILookup<int, TitleReleaseSourceData>> ReadSourcesAsync(int titleId, int[] groupKeys, int after, int limit, CancellationToken ct)
    {
        // Apply the lookahead limit to each requested group before returning any rows. Keep the
        // exact asserting claim match used by ReleaseGroups, with a separate lookup for negative keys.
        var rows = await _context.Database.SqlQuery<ReleaseSourceRow>($"""
            -- Admin title release provenance
            SELECT requested."GroupKey", source."DatGameId", source."DatFileId", source."DatName", source."GameName"
            FROM unnest({groupKeys}) AS requested("GroupKey")
            CROSS JOIN LATERAL (
                SELECT game."Id" AS "DatGameId", dat."Id" AS "DatFileId", dat."Name" AS "DatName", game."Name" AS "GameName"
                FROM (
                    SELECT g."Id", g."SourceEntryId", g."DatFileId", g."Name"
                    FROM romd."CatalogReleaseSources" claim
                    JOIN romd."DatGames" g ON g."SourceEntryId" = claim."SourceEntryId"
                        AND g."Id"::text = claim."ProviderClaimKey"
                    WHERE requested."GroupKey" >= 0 AND claim."CatalogReleaseId" = requested."GroupKey"
                    UNION ALL
                    SELECT g."Id", g."SourceEntryId", g."DatFileId", g."Name"
                    FROM romd."DatGames" g
                    WHERE requested."GroupKey" < 0 AND g."Id" = -requested."GroupKey"
                ) game
                JOIN romd."TitleSourceLinks" link ON link."SourceEntryId" = game."SourceEntryId"
                JOIN romd."DatFiles" dat ON dat."Id" = game."DatFileId"
                WHERE link."TitleId" = {titleId} AND game."Id" > {after}
                ORDER BY game."Id"
                LIMIT {limit + 1}
            ) source
            ORDER BY requested."GroupKey", source."DatGameId"
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
