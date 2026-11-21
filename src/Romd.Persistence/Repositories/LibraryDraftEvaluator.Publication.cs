using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Persistence.Repositories;

public sealed partial class LibraryDraftEvaluator
{
    private const int ExampleLimit = 6;

    public async Task<ErrorOr<LibraryPublicationReview>> ReviewPublicationAsync(int libraryId, CancellationToken ct)
    {
        var library = await libraries.GetByIdAsync(libraryId, ct);
        if (library is null)
            return LibraryErrors.NotFound();
        if (!library.HasValidConfiguration)
            return Error.Conflict("Libraries.InvalidBaseline", "Repair the saved library rules before publishing.");

        var config = library.Configuration;
        var handpicked = config.TitleSelectionMode == Romd.Domain.Libraries.LibraryTitleSelectionMode.IncludeOnly;
        var dirty = await catalog.GetPlatformIdsNeedingRebuildAsync(ct);
        if (dirty.Count > 0)
        {
            var dependencySystems = handpicked
                ? await libraries.GetHandpickedPlatformIdsAsync(config.IncludeTitleIds, ct)
                : config.AllowedPlatformIds;
            if ((!handpicked && dependencySystems.Count == 0) || dependencySystems.Any(dirty.Contains))
                return Error.Conflict("Libraries.CatalogUpdating", "A system used by this library is updating. Review changes again when it finishes.");
        }

        var checkedAt = DateTimeOffset.UtcNow;
        var published = context.MaterializedLibraryTitles.Where(title => title.LibraryId == libraryId &&
            title.IsOwned && library.LastMaterializedAt.HasValue);
        var attachments = context.LibraryCollections.Where(attachment => attachment.LibraryId == libraryId);
        var publishedShelves = await attachments
            .Where(attachment => context.CollectionItems.Any(item => item.CollectionId == attachment.CollectionId &&
                published.Any(title => title.TitleId == item.TitleId)))
            .Join(context.Collections, attachment => attachment.CollectionId, collection => collection.Id,
                (_, collection) => new { collection.Id, collection.Name })
            .OrderBy(collection => collection.Name).ThenBy(collection => collection.Id).ToListAsync(ct);
        var shelvesToCheck = publishedShelves.Select(shelf => shelf.Id).ToArray();
        var shelvesStillVisible = new HashSet<int>();

        // Include previously visible titles even if the new system or handpicked scope excludes them.
        var titles = context.Titles.Where(title => published.Any(row => row.TitleId == title.Id) ||
            ((handpicked ? config.IncludeTitleIds.Contains(title.Id) :
                config.AllowedPlatformIds.Count == 0 || config.AllowedPlatformIds.Contains(title.PlatformId)) &&
             (config.IncludeUntrackedTitles || context.TrackedTitles.Any(tracked => tracked.TitleId == title.Id))));
        var addedExamples = new List<LibraryPublicationGame>(ExampleLimit);
        var removedExamples = new List<LibraryPublicationGame>(ExampleLimit);
        int lastId = 0, publishedCount = 0, projectedCount = 0, added = 0, removed = 0;
        while (true)
        {
            var batch = await titles.Where(title => title.Id > lastId).OrderBy(title => title.Id)
                .Select(title => new { title.Id, title.Name, WasVisible = published.Any(row => row.TitleId == title.Id) })
                .Take(titleBatchSize).ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var ids = batch.Select(title => title.Id).ToArray();
            var candidates = await dataProvider.GetCandidatesAsync(ReadScope(ids), ct);
            var byId = candidates.ToDictionary(candidate => candidate.TitleId);
            var owned = MaterializedLibraryProjectionBuilder.Build(libraryId, candidates, config).Titles
                .Where(title => title.IsOwned).Select(title => title.TitleId).ToHashSet();
            projectedCount += owned.Count;
            foreach (var row in batch)
            {
                var willBeVisible = owned.Contains(row.Id);
                if (row.WasVisible)
                    publishedCount++;
                if (row.WasVisible == willBeVisible)
                    continue;
                if (willBeVisible)
                {
                    added++;
                    if (addedExamples.Count < ExampleLimit)
                        addedExamples.Add(new(IdCoder.Encode(row.Id), row.Name, null));
                }
                else
                {
                    removed++;
                    if (removedExamples.Count < ExampleLimit)
                        removedExamples.Add(new(IdCoder.Encode(row.Id), row.Name,
                            byId.TryGetValue(row.Id, out var candidate)
                                ? MaterializedLibraryProjectionBuilder.GetExclusionReason(candidate, config) ?? "MissingRom"
                                : "NoLinkedRelease"));
                }
            }
            if (owned.Count > 0 && shelvesStillVisible.Count < shelvesToCheck.Length)
                shelvesStillVisible.UnionWith(await context.CollectionItems
                    .Where(item => shelvesToCheck.Contains(item.CollectionId) && owned.Contains(item.TitleId))
                    .Select(item => item.CollectionId).Distinct().ToListAsync(ct));
            lastId = batch[^1].Id;
        }

        var emptied = publishedShelves.Where(shelf => !shelvesStillVisible.Contains(shelf.Id)).ToArray();
        int accounts = await context.Users.CountAsync(user => user.LibraryId == libraryId, ct);
        return new LibraryPublicationReview(library.MaterializationRevision, checkedAt, library.LastMaterializedAt, publishedCount,
            projectedCount, accounts, added, removed, addedExamples, removedExamples, emptied.Length,
            emptied.Take(ExampleLimit).Select(shelf => shelf.Name).ToArray());
    }
}
