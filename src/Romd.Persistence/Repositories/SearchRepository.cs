using Romd.Application.Common.Artwork;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;
using Romd.Persistence.Extensions;
using Romd.Persistence.Queries;
using Romd.Persistence.Search;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Repository implementation for full-text search over the PostgreSQL <c>tsvector</c>
///     projections. Every sort uses keyset pagination: Name/Year/Rating seek on the sort column
///     plus id, and Relevance seeks on <c>(ts_rank, id)</c>, so pages never repeat or skip rows.
/// </summary>
public sealed class SearchRepository(RomdDbContext context, IArtworkReader artworkReader) : ISearchRepository
{
    public SearchRepository(RomdDbContext context) : this(context, new ArtworkReader(context)) { }

    public async Task<PagedList<DatGame>> SearchGamesAsync(
        string? query,
        GameSearchFilters filters,
        GameSortField sortField,
        string? cursor,
        int limit,
        int? libraryId,
        CancellationToken cancellationToken = default)
    {
        // Decode cursor (opaque to clients - they don't know the underlying strategy)
        var cursorData = CursorUtils.FromCursor<CompositeCursor>(cursor);

        // Validate cursor sort field matches request - if mismatch, start from beginning
        if (cursorData is not null && cursorData.SortField != sortField)
        {
            cursorData = null;
        }

        bool useSearch = !string.IsNullOrWhiteSpace(query);
        string? tsQuery = useSearch ? SearchQuery.ToTsQueryText(query!) : null;

        var baseQuery = useSearch
            ? SearchQuery.MatchGames(context.DatGames.AsNoTracking(), query!)
            : context.DatGames.AsNoTracking();

        // Apply filters (platform, year, manufacturer, region, bios)
        baseQuery = ApplyFilters(baseQuery, filters);

        // Apply library filtering via resolver-recommended release projection rows.
        baseQuery = baseQuery.ApplyLibrary(libraryId, context.MaterializedLibraryReleases, context.Libraries);

        List<DatGameEntity> entities;
        string? nextCursor;

        if (sortField == GameSortField.Relevance && tsQuery is not null)
        {
            (entities, nextCursor) = await ExecuteGamesWithRelevancePaginationAsync(
                baseQuery, tsQuery, cursorData, limit, cancellationToken);
        }
        else
        {
            (entities, nextCursor) = await ExecuteWithKeysetPaginationAsync(
                baseQuery, cursorData, sortField, limit, cancellationToken);
        }

        // Map to domain models
        var domainItems = entities.Select(e => e.ToDomain()).ToList();

        return new PagedList<DatGame>(domainItems, nextCursor, nextCursor is not null);
    }

    private static async Task<(List<DatGameEntity> Entities, string? NextCursor)>
        ExecuteGamesWithRelevancePaginationAsync(
            IQueryable<DatGameEntity> query,
            string tsQuery,
            CompositeCursor? cursorData,
            int limit,
            CancellationToken cancellationToken)
    {
        var ranked = query
            .Include(g => g.Roms)
            .Include(g => g.Disks)
            .Select(g => new RankedGame
            {
                Game = g,
                Rank = g.SearchVector.Rank(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery))
            });

        if (cursorData is not null && SearchQuery.TryParseRank(cursorData.SortValue, out float lastRank))
        {
            int lastId = cursorData.Id;
            ranked = ranked.Where(row =>
                row.Rank < lastRank ||
                (row.Rank == lastRank && row.Game.Id > lastId));
        }

        var rows = await ranked
            .OrderByDescending(row => row.Rank)
            .ThenBy(row => row.Game.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var lastRow = rows[limit - 1];
            rows.RemoveAt(limit);
            nextCursor = CursorUtils.ToCursor(new CompositeCursor(
                GameSortField.Relevance,
                SearchQuery.FormatRank(lastRow.Rank),
                lastRow.Game.Id));
        }

        return (rows.Select(row => row.Game).ToList(), nextCursor);
    }

    private static async Task<(List<DatGameEntity> Entities, string? NextCursor)> ExecuteWithKeysetPaginationAsync(
        IQueryable<DatGameEntity> query,
        CompositeCursor? cursorData,
        GameSortField sortField,
        int limit,
        CancellationToken cancellationToken)
    {
        // Apply cursor seek (WHERE clause for keyset)
        query = ApplyKeysetCursorSeek(query, cursorData, sortField);

        // Apply deterministic ordering
        query = ApplyOrdering(query, sortField);

        // Execute with limit + 1 to detect next page
        var entities = await query
            .Take(limit + 1)
            .Include(g => g.Roms)
            .Include(g => g.Disks)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        bool hasNextPage = entities.Count > limit;

        if (hasNextPage)
        {
            // Get the last valid item (before the peek item)
            var lastItem = entities[limit - 1];

            // Remove the "peek" item
            entities.RemoveAt(limit);

            // Next cursor contains the sort value and ID of the last item
            string? sortValue = GetSortValue(lastItem, sortField);
            var nextCursorData = new CompositeCursor(
                sortField,
                sortValue,
                lastItem.Id);
            nextCursor = CursorUtils.ToCursor(nextCursorData);
        }

        return (entities, nextCursor);
    }

    private IQueryable<DatGameEntity> ApplyFilters(
        IQueryable<DatGameEntity> query,
        GameSearchFilters filters)
    {
        // Platform filter - filter by DAT's platform via subquery
        if (filters.PlatformId.HasValue)
        {
            int platformId = filters.PlatformId.Value;
            query = query.Where(g =>
                context.DatFiles.Any(d => d.Id == g.DatFileId && d.PlatformId == platformId));
        }

        // Year filter
        if (!string.IsNullOrWhiteSpace(filters.Year))
        {
            query = query.Where(g => g.Year == filters.Year);
        }

        // Manufacturer filter
        if (!string.IsNullOrWhiteSpace(filters.Manufacturer))
        {
            query = query.Where(g => g.Manufacturer == filters.Manufacturer);
        }

        // Region filter via junction table
        if (filters.RegionId.HasValue)
        {
            var regionId = filters.RegionId.Value;
            query = query.Where(g =>
                context.DatGameRegions.Any(dgr => dgr.DatGameId == g.Id && dgr.RegionId == regionId));
        }

        // BIOS filter
        query = filters.BiosFilter switch
        {
            BiosFilter.Exclude => query.Where(g => !g.IsBios),
            BiosFilter.Only => query.Where(g => g.IsBios),
            _ => query // Include: no filter
        };

        return query;
    }

    private static IQueryable<DatGameEntity> ApplyKeysetCursorSeek(
        IQueryable<DatGameEntity> query,
        CompositeCursor? cursorData,
        GameSortField sortField)
    {
        if (cursorData is null)
        {
            return query;
        }

        return sortField switch
        {
            GameSortField.Name => ApplyNameCursorSeek(query, cursorData),
            GameSortField.Year => ApplyYearCursorSeek(query, cursorData),
            _ => query
        };
    }

    private static IQueryable<DatGameEntity> ApplyNameCursorSeek(
        IQueryable<DatGameEntity> query,
        CompositeCursor cursor)
    {
        if (cursor.SortValue is null)
        {
            return query;
        }

        string sortName = cursor.SortValue;
        int lastId = cursor.Id;

        return query.Where(g =>
            g.Name.CompareTo(sortName) > 0 ||
            (g.Name == sortName && g.Id > lastId));
    }

    private static IQueryable<DatGameEntity> ApplyYearCursorSeek(
        IQueryable<DatGameEntity> query,
        CompositeCursor cursor)
    {
        int lastId = cursor.Id;

        // If cursor year is null, we were at a null year row, only filter by ID
        if (cursor.SortValue is null)
        {
            return query.Where(g => g.Year == null && g.Id > lastId);
        }

        string sortYear = cursor.SortValue;

        // Year DESC with NULLs last:
        // - Non-null years less than cursor
        // - Same year with greater ID
        // - All null years (they come after all non-null years)
        return query.Where(g =>
            g.Year == null ||
            (g.Year != null && g.Year.CompareTo(sortYear) < 0) ||
            (g.Year == sortYear && g.Id > lastId));
    }

    private static IQueryable<DatGameEntity> ApplyOrdering(
        IQueryable<DatGameEntity> query,
        GameSortField sortField)
    {
        return sortField switch
        {
            GameSortField.Year => query
                // Year DESC with NULLs last
                .OrderByDescending(g => g.Year != null)
                .ThenByDescending(g => g.Year)
                .ThenBy(g => g.Id),

            // Name, and Relevance without a query, order by name
            _ => query
                .OrderBy(g => g.Name)
                .ThenBy(g => g.Id)
        };
    }

    private static string? GetSortValue(DatGameEntity entity, GameSortField sortField)
    {
        return sortField switch
        {
            GameSortField.Name => entity.Name,
            GameSortField.Year => entity.Year,
            _ => entity.Name
        };
    }

    #region Title Search

    public async Task<PagedList<CatalogTitleData>> SearchTitlesAsync(
        string? query,
        TitleSearchFilters filters,
        TitleSortField sortField,
        string? cursor,
        int limit,
        int? libraryId,
        CancellationToken cancellationToken = default)
    {
        // Decode cursor
        var cursorData = CursorUtils.FromCursor<TitleCompositeCursor>(cursor);

        // Validate cursor sort field matches request
        if (cursorData is not null && cursorData.SortField != (int)sortField)
        {
            cursorData = null;
        }

        bool useSearch = !string.IsNullOrWhiteSpace(query);
        string? tsQuery = useSearch ? SearchQuery.ToTsQueryText(query!) : null;

        var baseQuery = useSearch
            ? SearchQuery.MatchTitles(context.Titles.AsNoTracking(), query!)
            : context.Titles.AsNoTracking();

        // Apply filters (platform, genre, local payload)
        baseQuery = ApplyTitleFilters(baseQuery, filters);

        // Apply library filtering via materialized items
        baseQuery = baseQuery.ApplyLibrary(libraryId, context.MaterializedLibraryTitles, context.Libraries);

        List<CatalogTitleData> items;
        string? nextCursor;

        if (sortField == TitleSortField.Relevance && tsQuery is not null)
        {
            (items, nextCursor) = await ExecuteTitlesWithRelevancePaginationAsync(
                baseQuery, tsQuery, cursorData, filters.ReleaseCompleteness, limit, cancellationToken);
        }
        else
        {
            (items, nextCursor) = await ExecuteTitlesWithKeysetPaginationAsync(
                baseQuery, cursorData, sortField, filters.ReleaseCompleteness, limit, cancellationToken);
        }

        // Resolve only the selected page, once: artwork does not alter keyset/relevance cursors.
        if (items.Count > 0)
        {
            var artwork = await artworkReader.ResolveAsync(items.Select(item => item.Id).ToArray(), cancellationToken);
            items = items.Select(item => item with
            {
                Artwork = artwork.TryGetValue(item.Id, out var resolved) ? resolved : []
            }).ToList();
        }
        return new PagedList<CatalogTitleData>(items, nextCursor, nextCursor is not null);
    }

    private IQueryable<TitleEntity> ApplyTitleFilters(
        IQueryable<TitleEntity> query,
        TitleSearchFilters filters)
    {
        // Platform filter
        if (filters.PlatformId.HasValue)
        {
            query = query.Where(t => t.PlatformId == filters.PlatformId.Value);
        }

        // Genre filter
        if (!string.IsNullOrWhiteSpace(filters.Genre))
        {
            query = query.Where(t => t.Genre == filters.Genre);
        }

        // Enrichment status filter
        if (!string.IsNullOrWhiteSpace(filters.EnrichmentStatus))
        {
            query = query.Where(t => t.EnrichmentStatus == filters.EnrichmentStatus);
        }

        // Tracked state is durable intent represented by row existence, not a Title column.
        query = filters.Tracked switch
        {
            TrackedFilter.Tracked => query.Where(t => context.TrackedTitles.Any(tracked => tracked.TitleId == t.Id)),
            TrackedFilter.Untracked => query.Where(t => !context.TrackedTitles.Any(tracked => tracked.TitleId == t.Id)),
            _ => query
        };

        // Release completeness is applied after projection because it is deliberately a
        // DAT-release read model. It is separate from the provider-neutral title-level
        // HasLocalPayload fact projected directly from TitleEntity below.

        return query;
    }

    private async Task<(List<CatalogTitleData> Items, string? NextCursor)> ExecuteTitlesWithRelevancePaginationAsync(
        IQueryable<TitleEntity> query,
        string tsQuery,
        TitleCompositeCursor? cursorData,
        ReleaseCompletenessFilter releaseCompleteness,
        int limit,
        CancellationToken cancellationToken)
    {
        var ranked = ApplyReleaseCompletenessFilter(
            ProjectToRankedCatalogTitles(query, tsQuery),
            releaseCompleteness);

        if (cursorData is not null && SearchQuery.TryParseRank(cursorData.SortValue, out float lastRank))
        {
            int lastId = cursorData.Id;
            ranked = ranked.Where(row =>
                row.Rank < lastRank ||
                (row.Rank == lastRank && row.Item.Id > lastId));
        }

        var rows = await ranked
            .OrderByDescending(row => row.Rank)
            .ThenBy(row => row.Item.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var lastRow = rows[limit - 1];
            rows.RemoveAt(limit);
            nextCursor = CursorUtils.ToCursor(new TitleCompositeCursor(
                (int)TitleSortField.Relevance,
                SearchQuery.FormatRank(lastRow.Rank),
                lastRow.Item.Id));
        }

        return (rows.Select(row => row.Item).ToList(), nextCursor);
    }

    private async Task<(List<CatalogTitleData> Items, string? NextCursor)> ExecuteTitlesWithKeysetPaginationAsync(
        IQueryable<TitleEntity> query,
        TitleCompositeCursor? cursorData,
        TitleSortField sortField,
        ReleaseCompletenessFilter releaseCompleteness,
        int limit,
        CancellationToken cancellationToken)
    {
        query = ApplyTitleKeysetCursorSeek(query, cursorData, sortField);
        query = ApplyTitleOrdering(query, sortField);

        // Single query with LEFT JOIN
        var projected = ApplyReleaseCompletenessFilter(
            ProjectToRankedCatalogTitles(query, tsQuery: null),
            releaseCompleteness);

        var items = await projected
            .Select(row => row.Item)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        bool hasNextPage = items.Count > limit;

        if (hasNextPage)
        {
            var lastItem = items[limit - 1];
            items = items.Take(limit).ToList();
            string? sortValue = GetTitleSortValue(lastItem, sortField);
            var nextCursorData = new TitleCompositeCursor(
                (int)sortField,
                sortValue,
                lastItem.Id);
            nextCursor = CursorUtils.ToCursor(nextCursorData);
        }

        return (items, nextCursor);
    }

    private IQueryable<RankedCatalogTitle> ProjectToRankedCatalogTitles(
        IQueryable<TitleEntity> query,
        string? tsQuery)
    {
        return
            from t in query
            let totalVersions = (
                from l in context.EffectiveTitleSourceLinks()
                where l.TitleId == t.Id
                join g in context.DatGames on l.SourceEntryId equals g.SourceEntryId
                select g.Id
            ).Count()
            let localPayloadVersions = (
                from l in context.EffectiveTitleSourceLinks()
                where l.TitleId == t.Id
                join g in context.DatGames on l.SourceEntryId equals g.SourceEntryId
                join r in context.DatRoms on g.Id equals r.DatGameId
                where r.RomFileId != null
                select g.Id
            ).Distinct().Count()
            select new RankedCatalogTitle
            {
                Item = new CatalogTitleData
                {
                    Id = t.Id,
                    PlatformId = t.PlatformId,
                    Name = t.Name,
                    EnrichmentStatus = t.EnrichmentStatus,
                    Genre = t.Genre,
                    ReleaseDate = t.ReleaseDate,
                    Rating = t.Rating,
                    TotalVersionCount = totalVersions,
                    LocalPayloadVersionCount = localPayloadVersions,
                    HasLocalPayload = t.HasLocalPayload,
                    IsTracked = context.TrackedTitles.Any(tracked => tracked.TitleId == t.Id),
                    CoverMediaId = context.TitleMedia
                        .Where(med => med.TitleId == t.Id && med.Type == "Cover")
                        .OrderByDescending(med => med.IsPrimary)
                        .Select(med => med.Id)
                        .FirstOrDefault()
                },
                Rank = tsQuery == null
                    ? 0f
                    : t.SearchVector.Rank(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery))
            };
    }

    private static IQueryable<RankedCatalogTitle> ApplyReleaseCompletenessFilter(
        IQueryable<RankedCatalogTitle> query,
        ReleaseCompletenessFilter releaseCompleteness)
    {
        return releaseCompleteness switch
        {
            ReleaseCompletenessFilter.Complete => query.Where(row =>
                row.Item.TotalVersionCount > 0 &&
                row.Item.LocalPayloadVersionCount == row.Item.TotalVersionCount),
            ReleaseCompletenessFilter.Partial => query.Where(row =>
                row.Item.LocalPayloadVersionCount > 0 &&
                row.Item.LocalPayloadVersionCount < row.Item.TotalVersionCount),
            ReleaseCompletenessFilter.None => query.Where(row =>
                row.Item.TotalVersionCount > 0 &&
                row.Item.LocalPayloadVersionCount == 0),
            _ => query
        };
    }

    private static IQueryable<TitleEntity> ApplyTitleKeysetCursorSeek(
        IQueryable<TitleEntity> query,
        TitleCompositeCursor? cursorData,
        TitleSortField sortField)
    {
        if (cursorData is null)
        {
            return query;
        }

        return sortField switch
        {
            TitleSortField.Name => ApplyTitleNameCursorSeek(query, cursorData),
            TitleSortField.Rating => ApplyTitleRatingCursorSeek(query, cursorData),
            _ => query
        };
    }

    private static IQueryable<TitleEntity> ApplyTitleNameCursorSeek(
        IQueryable<TitleEntity> query,
        TitleCompositeCursor cursor)
    {
        if (cursor.SortValue is null)
        {
            return query;
        }

        string sortName = cursor.SortValue;
        int lastId = cursor.Id;

        return query.Where(t =>
            t.Name.CompareTo(sortName) > 0 ||
            (t.Name == sortName && t.Id > lastId));
    }

    private static IQueryable<TitleEntity> ApplyTitleRatingCursorSeek(
        IQueryable<TitleEntity> query,
        TitleCompositeCursor cursor)
    {
        int lastId = cursor.Id;

        // If cursor rating is null, we were at a null rating row
        if (cursor.SortValue is null)
        {
            return query.Where(t => t.Rating == null && t.Id > lastId);
        }

        if (!double.TryParse(cursor.SortValue, out double sortRating))
        {
            return query;
        }

        // Rating DESC with NULLs last
        return query.Where(t =>
            t.Rating == null ||
            (t.Rating != null && t.Rating < sortRating) ||
            (t.Rating == sortRating && t.Id > lastId));
    }

    private static IQueryable<TitleEntity> ApplyTitleOrdering(
        IQueryable<TitleEntity> query,
        TitleSortField sortField)
    {
        return sortField switch
        {
            TitleSortField.Rating => query
                // Rating DESC with NULLs last
                .OrderByDescending(t => t.Rating != null)
                .ThenByDescending(t => t.Rating)
                .ThenBy(t => t.Id),

            // Name, and Relevance without a query, order by name
            _ => query
                .OrderBy(t => t.Name)
                .ThenBy(t => t.Id)
        };
    }

    private static string? GetTitleSortValue(CatalogTitleData item, TitleSortField sortField)
    {
        return sortField switch
        {
            TitleSortField.Name => item.Name,
            TitleSortField.Rating => item.Rating?.ToString(),
            _ => item.Name
        };
    }

    #endregion

    #region Catalog Filters

    public async Task<CatalogFiltersData> GetCatalogFiltersAsync(CancellationToken cancellationToken = default)
    {
        // Sequential awaits: the scoped DbContext is single-flight (ADR
        // docs/decisions/admin-use-case-transaction-event-boundaries.md, "DbContext Concurrency").
        // Facet reads are per-query consistent only; cross-query consistency is not promised.
        return new CatalogFiltersData
        {
            Genres = await AggregateGenresAsync(cancellationToken),
            Years = await AggregateYearsAsync(cancellationToken),
            Manufacturers = await AggregateManufacturersAsync(cancellationToken),
            Regions = await AggregateRegionsAsync(cancellationToken),
            Languages = await AggregateLanguagesAsync(cancellationToken),
            ContentRatings = await AggregateContentRatingsAsync(cancellationToken),
            Platforms = await AggregatePlatformsAsync(cancellationToken),
            EnrichmentStatuses = await AggregateEnrichmentStatusesAsync(cancellationToken)
        };
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateGenresAsync(CancellationToken ct)
    {
        return await context.Titles
            .Where(t => t.Genre != null)
            .GroupBy(t => t.Genre!)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateYearsAsync(CancellationToken ct)
    {
        return await context.Titles
            .Where(t => t.ReleaseDate != null)
            .GroupBy(t => t.ReleaseDate!.Value.Year)
            .Select(g => new FacetItemData { Value = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateManufacturersAsync(CancellationToken ct)
    {
        return await context.Titles
            .Where(t => t.Publisher != null)
            .GroupBy(t => t.Publisher!)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateRegionsAsync(CancellationToken ct)
    {
        return await context.DatGameRegions
            .Where(dgr => context.DatGames.Any(g =>
                g.Id == dgr.DatGameId &&
                context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId)))
            .Join(context.Regions, dgr => dgr.RegionId, r => r.Id, (dgr, r) => r)
            .GroupBy(r => r.Name)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateLanguagesAsync(CancellationToken ct)
    {
        return await context.DatGameLanguages
            .Where(dgl => context.DatGames.Any(g =>
                g.Id == dgl.DatGameId &&
                context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId)))
            .Join(context.GameLanguages, dgl => dgl.GameLanguageId, l => l.Id, (dgl, l) => l)
            .GroupBy(l => l.Name)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateContentRatingsAsync(CancellationToken ct)
    {
        var facets = await context.TitleContentRatings
            .GroupBy(r => new { r.Board, r.Code })
            .Select(g => new { g.Key.Board, g.Key.Code, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Board)
            .ThenBy(f => f.Code)
            .ToListAsync(ct);

        return facets
            .Select(f => new FacetItemData
            {
                Value = $"{(RatingBoard)f.Board} {f.Code}",
                Count = f.Count
            })
            .ToList();
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregatePlatformsAsync(CancellationToken ct)
    {
        return await context.Titles
            .Join(context.Platforms, t => t.PlatformId, p => p.Id, (t, p) => p.Name)
            .GroupBy(name => name)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderBy(f => f.Value)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<FacetItemData>> AggregateEnrichmentStatusesAsync(CancellationToken ct)
    {
        return await context.Titles
            .GroupBy(t => t.EnrichmentStatus)
            .Select(g => new FacetItemData { Value = g.Key, Count = g.Count() })
            .OrderBy(f => f.Value)
            .ToListAsync(ct);
    }

    #endregion

    private sealed class RankedGame
    {
        public required DatGameEntity Game { get; init; }
        public float Rank { get; init; }
    }

    private sealed class RankedCatalogTitle
    {
        public required CatalogTitleData Item { get; init; }
        public float Rank { get; init; }
    }
}
