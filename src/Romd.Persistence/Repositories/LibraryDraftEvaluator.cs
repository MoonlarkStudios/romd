using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Contracts.Common.Models;
using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;

using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

namespace Romd.Persistence.Repositories;

/// <summary>Read-only comparison of two policies over one PostgreSQL snapshot.</summary>
public sealed class LibraryDraftEvaluator : ILibraryDraftEvaluator
{
    internal const int DefaultTitleBatchSize = 2048;
    private readonly RomdDbContext context;
    private readonly ILibraryRepository libraries;
    private readonly ILibraryCandidateReader dataProvider;
    private readonly ICatalogProjectionService catalog;
    private readonly int titleBatchSize;

    public LibraryDraftEvaluator(RomdDbContext context, ILibraryRepository libraries,
        ILibraryCandidateReader dataProvider, ICatalogProjectionService catalog)
        : this(context, libraries, dataProvider, catalog, DefaultTitleBatchSize) { }

    // The harness compares bounded scopes without exposing a runtime policy/configuration knob.
    internal LibraryDraftEvaluator(RomdDbContext context, ILibraryRepository libraries,
        ILibraryCandidateReader dataProvider, ICatalogProjectionService catalog, int titleBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(titleBatchSize);
        this.context = context;
        this.libraries = libraries;
        this.dataProvider = dataProvider;
        this.catalog = catalog;
        this.titleBatchSize = titleBatchSize;
    }

    public async Task<ErrorOr<LibraryEvaluation>> EvaluateAsync(int libraryId,
        LibraryConfiguration configuration, string view, string? search, int afterId, CancellationToken ct, string? cursor = null, int limit = 48)
    {
        if (LibraryConfigurationValidator.Validate(configuration) is not null ||
            view is not ("matching" or "added" or "removed" or "excluded" or "all") ||
            search?.Length > 200 || afterId < 0)
            return LibraryErrors.InvalidConfiguration();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var library = await libraries.GetByIdAsync(libraryId, ct);
        if (library is null)
            return LibraryErrors.NotFound();
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("Libraries.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        string fingerprint = KeysetCursor.Fingerprint("admin.library.configuration.preview", libraryId, library.MaterializationRevision, configuration, view, search?.Trim().ToLowerInvariant(), "id:asc");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, p => p.IsValid);
        if (decoded.IsError)
            return FieldErrors.Validation("Libraries.InvalidCursor", "The cursor is invalid for this configuration preview.", "cursor");
        afterId = decoded.Value?.Id ?? afterId;
        if (!library.HasValidConfiguration)
            return Error.Conflict("Libraries.InvalidBaseline", "Repair the saved library rules before comparing changes.");
        if (await libraries.ValidateConfigurationReferencesAsync(configuration, ct, libraryId) is not null)
            return LibraryErrors.InvalidConfiguration();
        var dirtyPlatforms = await catalog.GetPlatformIdsNeedingRebuildAsync(ct);
        // Exclusion inspection spans the whole catalog, so do not serve partial explanations.
        if (dirtyPlatforms.Count > 0)
            return Error.Conflict("Libraries.CatalogUpdating", "The catalog is updating. Try evaluating again when it finishes.");

        var evaluatedAt = DateTimeOffset.UtcNow;
        int savedCount = 0, matchingCount = 0, addedCount = 0, removedCount = 0;
        int lastTitleId = 0;
        var removalReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var filtered = new List<(int Id, LibraryEvaluationTitle Item)>(limit + 1);
        string? term = search?.Trim();

        // Exact comparison counts still inspect every title. Only one bounded title batch
        // and the requested page survive at once; released projections are title-local.
        while (true)
        {
            var metadata = await context.Titles.AsNoTracking()
                .Where(title => title.Id > lastTitleId).OrderBy(title => title.Id)
                .Select(title => new EvaluationTitle(title.Id, title.Name, title.PlatformId, title.Genre,
                    context.TrackedTitles.Any(tracked => tracked.TitleId == title.Id)))
                .Take(titleBatchSize).ToListAsync(ct);
            if (metadata.Count == 0)
                break;
            var ids = Array.AsReadOnly(metadata.Select(title => title.Id).ToArray());
            var loaded = await dataProvider.GetCandidatesAsync(new LibraryConfiguration
            {
                IncludeUntrackedTitles = true,
                TitleSelectionMode = Romd.Domain.Libraries.LibraryTitleSelectionMode.IncludeOnly,
                IncludeTitleIds = ids
            }, ct);
            var candidatesById = loaded.ToDictionary(title => title.TitleId);
            var missingIds = metadata.Where(title => !candidatesById.ContainsKey(title.Id))
                .Select(title => title.Id).ToArray();
            var missingRatings = (missingIds.Length == 0 ? [] : await context.TitleContentRatings.AsNoTracking()
                .Where(rating => missingIds.Contains(rating.TitleId)).ToListAsync(ct))
                .ToLookup((TitleContentRatingEntity rating) => rating.TitleId);

            foreach (var title in metadata)
            {
                var candidate = candidatesById.GetValueOrDefault(title.Id) ?? new TitleCandidates(
                    title.Id, title.PlatformId, title.Genre,
                    missingRatings[title.Id].Select(rating => rating.ToDomain()).ToArray(), [], title.IsTracked);
                bool wasEligible = MaterializedLibraryProjectionBuilder.Build(libraryId, [candidate], library.Configuration)
                    .Titles.Count != 0;
                var projected = MaterializedLibraryProjectionBuilder.Build(libraryId, [candidate], configuration)
                    .Titles.FirstOrDefault();
                bool isEligible = projected is not null;
                string? reason = isEligible ? null : MaterializedLibraryProjectionBuilder.GetExclusionReason(candidate, configuration);
                if (wasEligible)
                    savedCount++;
                if (isEligible)
                    matchingCount++;
                if (isEligible && !wasEligible)
                    addedCount++;
                if (wasEligible && !isEligible)
                {
                    removedCount++;
                    string removalReason = reason ?? "NoEligibleRelease";
                    removalReasons[removalReason] = removalReasons.GetValueOrDefault(removalReason) + 1;
                }

                bool matchesView = view switch
                {
                    "matching" => isEligible,
                    "added" => isEligible && !wasEligible,
                    "removed" => wasEligible && !isEligible,
                    "excluded" => !isEligible,
                    _ => true
                };
                if (filtered.Count > limit || title.Id <= afterId || !matchesView ||
                    !string.IsNullOrEmpty(term) && !title.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    continue;
                var basis = ContentRatingPolicyEvaluator.Evaluate(configuration.ContentRatingPolicy, candidate.ContentRatings).Basis;
                filtered.Add((title.Id, new LibraryEvaluationTitle(IdCoder.Encode(title.Id), title.Name,
                    string.Empty, null, wasEligible, isEligible, projected?.IsOwned ?? false, projected?.IsPlayable ?? false,
                    reason, basis?.Board.ToString(), basis?.Code, basis?.MinimumAge, 0)));
            }

            lastTitleId = metadata[^1].Id;
            if (metadata.Count < titleBatchSize)
                break;
        }
        // Exact comparison counts require eligibility across the catalog. Artwork and
        // collection counts do not: resolve those only for the displayed page.
        var pageIds = filtered.Take(limit).Select(r => r.Id).ToArray();
        var pageMetadata = await context.Titles.Where(t => pageIds.Contains(t.Id)).Select(t => new
        {
            t.Id,
            PlatformName = context.Platforms.Where(p => p.Id == t.PlatformId).Select(p => p.Name).First(),
            Cover = t.Media.Where(m => m.Type == "Cover" && m.IsPrimary).Select(m => (int?)m.Id).FirstOrDefault(),
            Collections = context.CollectionItems.Count(i => i.TitleId == t.Id &&
                context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == i.CollectionId))
        }).ToDictionaryAsync(t => t.Id, ct);
        var items = filtered.Take(limit).Select(r =>
        {
            var detail = pageMetadata[r.Id];
            return r.Item with
            {
                PlatformName = detail.PlatformName,
                CoverUrl = detail.Cover is int cover ? $"/media/{IdCoder.Encode(cover)}" : null,
                AttachedCollectionCount = detail.Collections
            };
        }).ToList();
        return new LibraryEvaluation(evaluatedAt, savedCount, matchingCount, addedCount, removedCount,
            removalReasons.Select(reason => new LibraryEvaluationReason(reason.Key, reason.Value))
                .OrderByDescending(reason => reason.Count).ToList(),
            new Page<LibraryEvaluationTitle>
            {
                Items = items,
                HasNextPage = filtered.Count > limit,
                NextCursor = filtered.Count > limit ? KeysetCursor.Encode(new IntegerPosition(filtered[limit - 1].Id), fingerprint) : null
            });
    }

    private sealed record EvaluationTitle(int Id, string Name, int PlatformId, string? Genre, bool IsTracked);
}
