using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Repository interface for full-text search operations.
/// </summary>
public interface ISearchRepository
{
    /// <summary>
    ///     Searches games with full-text search and composite keyset pagination.
    /// </summary>
    /// <param name="query">The search query string (optional). When null, returns all games matching filters.</param>
    /// <param name="filters">Filter parameters for narrowing results.</param>
    /// <param name="sortField">The field to sort results by.</param>
    /// <param name="cursor">Cursor for keyset pagination.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="libraryId">Library ID for content filtering (null for unrestricted).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged list of matching games.</returns>
    Task<PagedList<DatGame>> SearchGamesAsync(
        string? query,
        GameSearchFilters filters,
        GameSortField sortField,
        string? cursor,
        int limit,
        int? libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Searches titles with full-text search and composite keyset pagination.
    /// </summary>
    /// <param name="query">The search query string (optional). When null, returns all titles matching filters.</param>
    /// <param name="filters">Filter parameters for narrowing results.</param>
    /// <param name="sortField">The field to sort results by.</param>
    /// <param name="cursor">Cursor for keyset pagination.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="libraryId">Library ID for content filtering (null for unrestricted).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paged list of matching titles.</returns>
    Task<PagedList<CatalogTitleData>> SearchTitlesAsync(
        string? query,
        TitleSearchFilters filters,
        TitleSortField sortField,
        string? cursor,
        int limit,
        int? libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets aggregated filter facets with counts for catalog filtering.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated filter facets.</returns>
    Task<CatalogFiltersData> GetCatalogFiltersAsync(CancellationToken cancellationToken = default);
}
