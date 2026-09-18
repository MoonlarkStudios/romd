using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryPlatformFacets;

public sealed record GetLibraryPlatformFacetsQuery(int LibraryId, int MinimumItems)
    : IQuery<IReadOnlyList<LibraryFacetDto>>;

public sealed class GetLibraryPlatformFacetsQueryHandler(IReferenceCatalogService referenceCatalog, ILibraryRepository libraryRepository)
    : IQueryHandler<GetLibraryPlatformFacetsQuery, IReadOnlyList<LibraryFacetDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryFacetDto>>> HandleAsync(
        GetLibraryPlatformFacetsQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (await libraryRepository.GetByIdAsync(query.LibraryId, ct) is null)
        {
            return LibraryErrors.NotFound();
        }

        var facets = await libraryRepository.GetPlatformFacetsAsync(
            query.LibraryId,
            query.MinimumItems,
            ct);
        return facets.Select(facet => facet.ToContract(systemKeys)).ToList();
    }
}
