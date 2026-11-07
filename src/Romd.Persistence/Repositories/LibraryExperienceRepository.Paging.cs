using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Persistence.Repositories;

public sealed partial class LibraryExperienceRepository
{
    public async Task<ErrorOr<Page<CollectionLibraryPlacement>>> GetCollectionPlacementsPageAsync(int collectionId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Collections.AnyAsync(c => c.Id == collectionId, ct))
            return CollectionErrors.NotFound();
        if (limit is < 1 or > 100)
            return InvalidLimit();
        string fingerprint = KeysetCursor.Fingerprint("admin.collection.library-placements", collectionId, "libraryId:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return InvalidCursor();
        int after = decoded.Value?.Id ?? 0;
        var rows = await context.LibraryCollections.Where(a => a.CollectionId == collectionId && a.LibraryId > after)
            .Join(context.Libraries, a => a.LibraryId, l => l.Id, (a, l) => new { l.Id, l.Name, a.IsFeatured }).OrderBy(l => l.Id).Take(limit + 1).ToListAsync(ct);
        return new Page<CollectionLibraryPlacement> { Items = rows.Take(limit).Select(l => new CollectionLibraryPlacement(IdCoder.Encode(l.Id), l.Name, l.IsFeatured)).ToArray(), HasNextPage = rows.Count > limit, NextCursor = rows.Count > limit ? KeysetCursor.Encode(new IntegerPosition(rows[limit - 1].Id), fingerprint) : null };
    }

    public async Task<ErrorOr<Page<LibraryPreviewTitle>>> GetPreviewPageAsync(int libraryId, int? collectionId, string? cursor, int limit, CancellationToken ct)
    {
        var library = await context.Libraries.SingleOrDefaultAsync(l => l.Id == libraryId, ct);
        if (library is null)
            return LibraryErrors.NotFound();
        if (collectionId.HasValue && !await context.Collections.AnyAsync(c => c.Id == collectionId, ct))
            return CollectionErrors.NotFound();
        if (limit is < 1 or > 100)
            return InvalidLimit();
        string fingerprint = KeysetCursor.Fingerprint("admin.library.preview", libraryId, collectionId, library.MaterializationRevision, "sortOrder,titleId:asc");
        var decoded = KeysetCursor.Decode<PreviewPosition>(cursor, fingerprint, p => p.Id > 0);
        if (decoded.IsError)
            return InvalidCursor();
        int after = decoded.Value?.Id ?? 0;
        int afterOrder = decoded.Value?.Order ?? int.MinValue;
        var titles = context.MaterializedLibraryTitles.Where(t => t.LibraryId == libraryId && t.IsOwned && context.Libraries.Any(l => l.Id == libraryId && l.ConfigurationState == "Valid"));
        if (collectionId is int id)
            titles = titles.Where(t => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == id) && context.CollectionItems.Any(i => i.CollectionId == id && i.TitleId == t.TitleId));
        var ordered = titles.Join(context.Titles, m => m.TitleId, t => t.Id, (m, t) => new { Title = t, SortOrder = collectionId == null ? 0 : context.CollectionItems.Where(i => i.CollectionId == collectionId && i.TitleId == t.Id).Select(i => i.SortOrder).First() });
        var rows = await ordered.Where(x => x.SortOrder > afterOrder || x.SortOrder == afterOrder && x.Title.Id > after).OrderBy(x => x.SortOrder).ThenBy(x => x.Title.Id)
            .Select(x => new { x.Title.Id, x.Title.Name, x.SortOrder, PlatformName = context.Platforms.Where(p => p.Id == x.Title.PlatformId).Select(p => p.Name).First(), Cover = x.Title.Media.Where(m => m.Type == "Cover" && m.IsPrimary).Select(m => (int?)m.Id).FirstOrDefault() }).Take(limit + 1).ToListAsync(ct);
        return new Page<LibraryPreviewTitle> { Items = rows.Take(limit).Select(t => new LibraryPreviewTitle(IdCoder.Encode(t.Id), t.Name, t.PlatformName, t.Cover is int cover ? $"/media/{IdCoder.Encode(cover)}" : null)).ToArray(), HasNextPage = rows.Count > limit, NextCursor = rows.Count > limit ? KeysetCursor.Encode(new PreviewPosition(rows[limit - 1].SortOrder, rows[limit - 1].Id), fingerprint) : null };
    }
    private static Error InvalidLimit() => FieldErrors.Validation("Libraries.InvalidLimit", "Use a limit between 1 and 100.", "limit");
    private static Error InvalidCursor() => FieldErrors.Validation("Libraries.InvalidCursor", "The cursor is invalid for this query.", "cursor");
    private sealed record PreviewPosition(int Order, int Id);
}
