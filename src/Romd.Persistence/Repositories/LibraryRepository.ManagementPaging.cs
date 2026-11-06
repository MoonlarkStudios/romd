using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Domain.Libraries;

namespace Romd.Persistence.Repositories;

public sealed partial class LibraryRepository
{
    public async Task<ErrorOr<PagedList<Library>>> GetPageAsync(string? query, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return InvalidPageLimit();
        string? term = query?.Trim().ToLowerInvariant();
        string fingerprint = KeysetCursor.Fingerprint("admin.libraries", term, "name,id:asc");
        var decoded = KeysetCursor.Decode<LibraryPosition>(cursor, fingerprint, p => p.Id > 0 && p.Name is not null);
        if (decoded.IsError)
            return InvalidPageCursor();
        var libraries = context.Libraries.AsNoTracking();
        if (!string.IsNullOrEmpty(term))
            libraries = libraries.Where(l => l.Name.ToLower().Contains(term));
        if (decoded.Value is { } position)
            libraries = libraries.Where(l => string.Compare(l.Name, position.Name) > 0 || l.Name == position.Name && l.Id > position.Id);
        var rows = await libraries.OrderBy(l => l.Name).ThenBy(l => l.Id).Take(limit + 1).ToListAsync(ct);
        return new PagedList<Library>(rows.Take(limit).Select(l => l.ToDomain()).ToArray(), rows.Count > limit ? KeysetCursor.Encode(new LibraryPosition(rows[limit - 1].Name, rows[limit - 1].Id), fingerprint) : null, rows.Count > limit);
    }

    public async Task<ErrorOr<PagedList<GenreFacet>>> GetGenrePageAsync(int libraryId, int minimumItems, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Libraries.AnyAsync(l => l.Id == libraryId, ct))
            return LibraryErrors.NotFound();
        if (limit is < 1 or > 100)
            return InvalidPageLimit();
        if (minimumItems < 0)
            return FieldErrors.Validation("Libraries.InvalidMinimumItems", "Use a nonnegative minimum.", "minimumItems");
        string fingerprint = KeysetCursor.Fingerprint("admin.library.genres", libraryId, minimumItems, "genre:asc");
        var decoded = KeysetCursor.Decode<GenrePosition>(cursor, fingerprint, p => p.Genre is not null);
        if (decoded.IsError)
            return InvalidPageCursor();
        var query = MaterializedTitlesForValidLibrary(libraryId).Where(m => m.IsOwned && m.Genre != null)
            .GroupBy(m => m.Genre!).Select(g => new { Genre = g.Key, Count = g.Count() }).Where(g => g.Count >= minimumItems);
        if (decoded.Value is { } position)
            query = query.Where(g => string.Compare(g.Genre, position.Genre) > 0);
        var rows = await query.OrderBy(g => g.Genre).Take(limit + 1).ToListAsync(ct);
        return new PagedList<GenreFacet>(rows.Take(limit).Select(g => new GenreFacet(g.Genre, g.Count)).ToArray(), rows.Count > limit ? KeysetCursor.Encode(new GenrePosition(rows[limit - 1].Genre), fingerprint) : null, rows.Count > limit);
    }

    public async Task<ErrorOr<PagedList<CollectionFacet>>> GetCollectionPageAsync(int libraryId, int minimumItems, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Libraries.AnyAsync(l => l.Id == libraryId, ct))
            return LibraryErrors.NotFound();
        if (limit is < 1 or > 100)
            return InvalidPageLimit();
        if (minimumItems < 0)
            return FieldErrors.Validation("Libraries.InvalidMinimumItems", "Use a nonnegative minimum.", "minimumItems");
        string fingerprint = KeysetCursor.Fingerprint("admin.library.collections", libraryId, minimumItems, "collectionId:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidPageCursor();
        int after = decoded.Value?.Id ?? 0;
        var materialized = MaterializedTitlesForValidLibrary(libraryId);
        var rows = await context.Collections.Where(c => c.Id > after && context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == c.Id))
            .Select(c => new { c.Id, c.Name, Total = c.Items.Count, Matching = c.Items.Count(item => materialized.Any(m => m.IsOwned && m.TitleId == item.TitleId)) })
            .Where(c => c.Matching >= minimumItems).OrderBy(c => c.Id).Take(limit + 1).ToListAsync(ct);
        return new PagedList<CollectionFacet>(rows.Take(limit).Select(c => new CollectionFacet(c.Id, c.Name, c.Matching, c.Total)).ToArray(), rows.Count > limit ? KeysetCursor.Encode(new IntegerPosition(rows[limit - 1].Id), fingerprint) : null, rows.Count > limit);
    }

    public async Task<ErrorOr<PagedList<LibraryTitleReleaseDiagnostics>>> GetReleaseDiagnosticsPageAsync(int libraryId, int titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Libraries.AnyAsync(l => l.Id == libraryId, ct))
            return LibraryErrors.NotFound();
        if (!await context.Titles.AnyAsync(t => t.Id == titleId, ct))
            return Romd.Admin.Application.Catalog.CatalogErrors.TitleNotFound();
        if (limit is < 1 or > 100)
            return InvalidPageLimit();
        string fingerprint = KeysetCursor.Fingerprint("admin.library.title.releases", libraryId, titleId, "releaseId:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidPageCursor();
        int after = decoded.Value?.Id ?? 0;
        var ids = await context.MaterializedLibraryReleases.Where(r => r.LibraryId == libraryId && r.TitleId == titleId && r.DatGameId > after).OrderBy(r => r.DatGameId).Select(r => r.DatGameId).Take(limit + 1).ToArrayAsync(ct);
        var rows = await GetReleaseDiagnosticsCoreAsync(libraryId, titleId, ids.Take(limit).ToArray(), ct);
        return new PagedList<LibraryTitleReleaseDiagnostics>(rows.OrderBy(r => r.ReleaseId).ToArray(), ids.Length > limit ? KeysetCursor.Encode(new IntegerPosition(ids[limit - 1]), fingerprint) : null, ids.Length > limit);
    }

    private static Error InvalidPageLimit() => FieldErrors.Validation("Libraries.InvalidLimit", "Use a limit between 1 and 100.", "limit");
    private static Error InvalidPageCursor() => FieldErrors.Validation("Libraries.InvalidCursor", "The cursor is invalid for this query.", "cursor");
    private sealed record LibraryPosition(string Name, int Id);
    private sealed record GenrePosition(string Genre);
}
