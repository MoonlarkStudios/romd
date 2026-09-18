using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Search;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Catalog.Queries.GetCatalogFilters;

/// <summary>
///     Query to get aggregated filter facets for the catalog.
/// </summary>
public sealed record GetCatalogFiltersQuery : IQuery<CatalogFilters>;

/// <summary>
///     Handler for <see cref="GetCatalogFiltersQuery"/>.
/// </summary>
public sealed class GetCatalogFiltersQueryHandler(ISearchRepository searchRepository)
    : IQueryHandler<GetCatalogFiltersQuery, CatalogFilters>
{
    public async Task<ErrorOr<CatalogFilters>> HandleAsync(GetCatalogFiltersQuery query, CancellationToken ct = default)
    {
        var data = await searchRepository.GetCatalogFiltersAsync(ct);

        return new CatalogFilters
        {
            Genres = data.Genres.Select(ToContract).ToList(),
            Years = data.Years.Select(ToContract).ToList(),
            Manufacturers = data.Manufacturers.Select(ToContract).ToList(),
            Regions = data.Regions.Select(ToContract).ToList(),
            Languages = data.Languages.Select(ToContract).ToList(),
            ContentRatings = data.ContentRatings.Select(ToContract).ToList(),
            Platforms = data.Platforms.Select(ToContract).ToList(),
            EnrichmentStatuses = data.EnrichmentStatuses.Select(ToContract).ToList()
        };
    }

    private static FacetItem ToContract(Search.ReadModels.FacetItemData data) =>
        new() { Value = data.Value, Count = data.Count };
}
