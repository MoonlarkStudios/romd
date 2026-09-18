using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Persistence;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed class SourceEntryReader(RomdDbContext db) : ISourceEntryReader
{
    public async Task<ErrorOr<SourceEntryPage>> ReadAsync(int datId, int? after, int limit, string? search, int? titleId, CancellationToken ct)
    {
        if (!await db.DatFiles.AnyAsync(d => d.Id == datId, ct)) return Error.NotFound("SourceEntries.NotFound", "This source version no longer exists.");
        var term = search?.Trim().ToLowerInvariant();
        if (term?.Length > 200) return Error.Validation("SourceEntries.Search", "Use a search of 200 characters or fewer.");
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
        var size = Math.Clamp(limit, 1, 100);
        var items = await query.Take(size + 1).ToListAsync(ct);
        return new SourceEntryPage(items.Take(size).ToList(), items.Count > size ? items[size - 1].GameId : null);
    }
}
