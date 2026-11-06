using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Persistence;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed class SourceEntryReader(RomdDbContext db) : ISourceEntryReader
{
    public async Task<ErrorOr<PagedList<SourceEntryReference>>> ReadAsync(int datId, string? cursor, int limit, string? search, int? titleId, CancellationToken ct)
    {
        if (!await db.DatFiles.AnyAsync(d => d.Id == datId, ct))
            return Error.NotFound("SourceEntries.NotFound", "This source version no longer exists.");
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("SourceEntries.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        var term = search?.Trim().ToLowerInvariant();
        string fingerprint = KeysetCursor.Fingerprint("admin.dat.source-entries", datId, term, titleId, "gameId:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return FieldErrors.Validation("SourceEntries.InvalidCursor", "The cursor is invalid for this query.", "cursor");
        int? after = decoded.Value?.Id;
        if (term?.Length > 200)
            return FieldErrors.Validation("SourceEntries.Query", "Use a query of 200 characters or fewer.", "query");
        var query = from game in db.DatGames
                    join link in db.TitleSourceLinks on game.SourceEntryId equals link.SourceEntryId into links
                    from link in links.DefaultIfEmpty()
                    join title in db.Titles on link.TitleId equals title.Id into titles
                    from title in titles.DefaultIfEmpty()
                    where game.DatFileId == datId && (after == null || game.Id > after)
                        && (titleId == null || title.Id == titleId)
                        && (term == null || term == "" || game.Name.ToLower().Contains(term) || title.Name.ToLower().Contains(term))
                    orderby game.Id
                    select new SourceEntryReference(game.Id, game.SourceEntryId, game.Name, (int?)title.Id, title.Name,
                        game.Roms.Any(r => r.RomFileId != null),
                        db.TitleSourceLinks.Where(l => l.TitleId == title.Id)
                            .Join(db.EffectiveSourceEntries(), l => l.SourceEntryId, e => e.Id, (l, e) => e)
                            .Where(e => db.CatalogSources.Any(c => c.Id == e.CatalogSourceId && c.Kind != "Dat")
                                || db.DatGames.Any(g => g.SourceEntryId == e.Id && db.DatFiles.Any(d => d.Id == g.DatFileId && d.Lifecycle == "Active")))
                            .Select(e => e.CatalogSourceId).Distinct().Count());
        var items = await query.Take(limit + 1).ToListAsync(ct);
        return new PagedList<SourceEntryReference>(items.Take(limit).ToArray(), items.Count > limit ? KeysetCursor.Encode(new IntegerPosition(items[limit - 1].GameId), fingerprint) : null, items.Count > limit);
    }
}
