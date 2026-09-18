using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;

public sealed record GetLibraryCollectionsQuery(int LibraryId, int MinimumItems)
    : IQuery<IReadOnlyList<LibraryCollectionDto>>;

public sealed class GetLibraryCollectionsQueryHandler(ILibraryRepository libraryRepository)
    : IQueryHandler<GetLibraryCollectionsQuery, IReadOnlyList<LibraryCollectionDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryCollectionDto>>> HandleAsync(
        GetLibraryCollectionsQuery query,
        CancellationToken ct = default)
    {
        if (await libraryRepository.GetByIdAsync(query.LibraryId, ct) is null)
        {
            return LibraryErrors.NotFound();
        }

        var collections = await libraryRepository.GetCollectionFacetsAsync(
            query.LibraryId,
            query.MinimumItems,
            ct);
        return collections.Select(collection => collection.ToContract()).ToList();
    }
}
