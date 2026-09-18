using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;

public sealed record GetLibraryGenreFacetsQuery(int LibraryId, int MinimumItems)
    : IQuery<IReadOnlyList<LibraryFacetDto>>;

public sealed class GetLibraryGenreFacetsQueryHandler(ILibraryRepository libraryRepository)
    : IQueryHandler<GetLibraryGenreFacetsQuery, IReadOnlyList<LibraryFacetDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryFacetDto>>> HandleAsync(
        GetLibraryGenreFacetsQuery query,
        CancellationToken ct = default)
    {
        if (await libraryRepository.GetByIdAsync(query.LibraryId, ct) is null)
        {
            return LibraryErrors.NotFound();
        }

        var facets = await libraryRepository.GetGenreFacetsAsync(
            query.LibraryId,
            query.MinimumItems,
            ct);
        return facets.Select(facet => facet.ToContract()).ToList();
    }
}
