using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class LibraryExperienceRepository(RomdDbContext context) : ILibraryExperienceRepository
{
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetCollectionIdsByLibraryAsync(CancellationToken ct)
    {
        var rows = await context.LibraryCollections.OrderBy(a => a.SortOrder).ThenBy(a => a.CollectionId)
            .Select(a => new { a.LibraryId, a.CollectionId }).ToListAsync(ct);
        return rows.GroupBy(a => a.LibraryId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<string>)g.Select(a => IdCoder.Encode(a.CollectionId)).ToList());
    }

    public async Task<IReadOnlyList<CollectionLibraryPlacementDto>> GetCollectionPlacementsAsync(int collectionId, CancellationToken ct)
    {
        var rows = await context.LibraryCollections.Where(a => a.CollectionId == collectionId)
            .Join(context.Libraries, a => a.LibraryId, l => l.Id, (a, l) => new { l.Id, l.Name, a.IsFeatured })
            .OrderBy(l => l.Name).ToListAsync(ct);
        return rows.Select(l => new CollectionLibraryPlacementDto(IdCoder.Encode(l.Id), l.Name, l.IsFeatured)).ToList();
    }

    public async Task<IReadOnlyList<LibraryAttachmentDto>> GetAttachmentsAsync(int libraryId, CancellationToken ct)
    {
        var rows = await context.LibraryCollections.Where(x => x.LibraryId == libraryId)
            .Join(context.Collections, a => a.CollectionId, c => c.Id, (a, c) => new { Attachment = a, Collection = c })
            .OrderBy(x => x.Attachment.SortOrder).ThenBy(x => x.Collection.Id)
            .Select(x => new {
                x.Collection.Id, x.Collection.Name, x.Collection.Description, x.Collection.CoverMediaId,
                x.Attachment.SortOrder, x.Attachment.IsFeatured,
                Total = context.CollectionItems.Count(i => i.CollectionId == x.Collection.Id),
                Visible = context.CollectionItems.Count(i => i.CollectionId == x.Collection.Id &&
                    context.MaterializedLibraryTitles.Any(t => t.LibraryId == libraryId && t.TitleId == i.TitleId && t.IsOwned) &&
                    context.Libraries.Any(l => l.Id == libraryId && l.ConfigurationState == "Valid")),
                Libraries = context.LibraryCollections.Count(a => a.CollectionId == x.Collection.Id)
            }).ToListAsync(ct);
        return rows.Select(x => new LibraryAttachmentDto(IdCoder.Encode(x.Id), x.Name, x.Description,
            x.CoverMediaId is int cover ? $"/media/{IdCoder.Encode(cover)}" : null,
            x.SortOrder, x.IsFeatured, x.Visible, x.Total, x.Libraries)).ToList();
    }

    public async Task<ErrorOr<Success>> SetAttachmentsAsync(int libraryId, IReadOnlyList<(int CollectionId, bool IsFeatured)> items, CancellationToken ct)
    {
        if (items.Count > 500 || items.Select(x => x.CollectionId).Distinct().Count() != items.Count)
            return Error.Validation("Libraries.InvalidAttachments", "Choose each collection once, up to 500 collections.");
        // Serialize replacements per library, including an initially empty attachment list.
        int locked = await context.Libraries.Where(l => l.Id == libraryId)
            .ExecuteUpdateAsync(update => update.SetProperty(l => l.UpdatedAt, l => l.UpdatedAt), ct);
        if (locked == 0) return LibraryErrors.NotFound();
        var ids = items.Select(x => x.CollectionId).ToList();
        if (await context.Collections.CountAsync(c => ids.Contains(c.Id), ct) != ids.Count)
            return Error.Validation("Libraries.CollectionNotFound", "One or more collections no longer exist.");
        await context.LibraryCollections.Where(x => x.LibraryId == libraryId).ExecuteDeleteAsync(ct);
        foreach (var entry in context.ChangeTracker.Entries<LibraryCollectionEntity>()
                     .Where(e => e.Entity.LibraryId == libraryId).ToList())
            entry.State = EntityState.Detached;
        context.LibraryCollections.AddRange(items.Select((item, index) => new LibraryCollectionEntity {
            LibraryId = libraryId, CollectionId = item.CollectionId, SortOrder = index, IsFeatured = item.IsFeatured
        }));
        return Result.Success;
    }

    public async Task<LibraryPreviewDto> GetPreviewAsync(int libraryId, int? collectionId, int afterId, int afterOrder, CancellationToken ct)
    {
        var titles = context.MaterializedLibraryTitles.Where(t => t.LibraryId == libraryId && t.IsOwned &&
            context.Libraries.Any(l => l.Id == libraryId && l.ConfigurationState == "Valid"));
        if (collectionId is int id)
            titles = titles.Where(t => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == id) &&
                context.CollectionItems.Any(i => i.CollectionId == id && i.TitleId == t.TitleId));
        var ordered = titles.Join(context.Titles, m => m.TitleId, t => t.Id, (m, t) => new {
            Title = t,
            SortOrder = collectionId == null ? 0 : context.CollectionItems
                .Where(i => i.CollectionId == collectionId && i.TitleId == t.Id).Select(i => i.SortOrder).First()
        });
        var rows = await ordered.Where(x => x.SortOrder > afterOrder || (x.SortOrder == afterOrder && x.Title.Id > afterId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Title.Id)
            .Select(x => new { x.Title.Id, x.Title.Name, x.SortOrder,
                PlatformName = context.Platforms.Where(p => p.Id == x.Title.PlatformId).Select(p => p.Name).First(),
                Cover = x.Title.Media.Where(m => m.Type == "Cover" && m.IsPrimary).Select(m => (int?)m.Id).FirstOrDefault()
            }).Take(49).ToListAsync(ct);
        return new LibraryPreviewDto(rows.Take(48).Select(t => new LibraryPreviewTitleDto(IdCoder.Encode(t.Id), t.Name,
            t.PlatformName, t.Cover is int cover ? $"/media/{IdCoder.Encode(cover)}" : null)).ToList(),
            rows.Count > 48 ? $"{rows[47].SortOrder}:{IdCoder.Encode(rows[47].Id)}" : null);
    }
}
