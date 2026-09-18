using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Search.Queries.SearchGames;

/// <summary>
///     Query to search games with full-text search.
/// </summary>
public sealed record SearchGamesQuery : IQuery<PagedList<DatGame>>
{
    /// <summary>
    ///     The search query text. Optional; when null, returns filtered results without FTS.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    ///     Platform ID filter (already resolved from Sqid).
    /// </summary>
    public int? PlatformId { get; init; }

    /// <summary>
    ///     Release year filter.
    /// </summary>
    public string? Year { get; init; }

    /// <summary>
    ///     Manufacturer filter.
    /// </summary>
    public string? Manufacturer { get; init; }

    /// <summary>
    ///     Region ID filter (resolved from taxonomy).
    /// </summary>
    public int? RegionId { get; init; }

    /// <summary>
    ///     Filter for BIOS entries. Defaults to excluding BIOS.
    /// </summary>
    public BiosFilter BiosFilter { get; init; } = BiosFilter.Exclude;

    /// <summary>
    ///     The field to sort results by. Defaults to Name.
    /// </summary>
    public GameSortField SortBy { get; init; } = GameSortField.Name;

    /// <summary>
    ///     The cursor for pagination, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return.
    /// </summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
///     Handler for <see cref="SearchGamesQuery" />.
/// </summary>
public sealed class SearchGamesQueryHandler : IQueryHandler<SearchGamesQuery, PagedList<DatGame>>
{
    private readonly ICurrentUser _currentUser;
    private readonly ISearchRepository _searchRepository;

    public SearchGamesQueryHandler(
        ISearchRepository searchRepository,
        ICurrentUser currentUser)
    {
        _searchRepository = searchRepository;
        _currentUser = currentUser;
    }

    public async Task<ErrorOr<PagedList<DatGame>>> HandleAsync(SearchGamesQuery query, CancellationToken ct = default)
    {
        // Validate: Relevance sort requires a search query
        if (query.SortBy == GameSortField.Relevance && string.IsNullOrWhiteSpace(query.Query))
        {
            return SearchErrors.RelevanceSortRequiresQuery;
        }

        // Resolve library ID for user (Contributors+ bypass filtering)
        int? libraryId = _currentUser.HasRole(RomdRoleType.Contributor) ? null : _currentUser.LibraryId;

        // Build filters
        var filters = new GameSearchFilters(
            query.PlatformId,
            query.Year,
            query.Manufacturer,
            query.RegionId,
            query.BiosFilter);

        return await _searchRepository.SearchGamesAsync(
            query.Query,
            filters,
            query.SortBy,
            query.Cursor,
            query.Limit,
            libraryId,
            ct);
    }
}
