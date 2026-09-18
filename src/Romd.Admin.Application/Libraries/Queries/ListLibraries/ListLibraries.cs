using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.ListLibraries;

public sealed record ListLibrariesQuery : IQuery<IReadOnlyList<LibraryDto>>;

public sealed class ListLibrariesQueryHandler(IReferenceCatalogService referenceCatalog, ILibraryRepository libraryRepository, ILibraryExperienceRepository experienceRepository)
    : IQueryHandler<ListLibrariesQuery, IReadOnlyList<LibraryDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryDto>>> HandleAsync(
        ListLibrariesQuery query,
        CancellationToken ct = default)
    {
        var libraries = await libraryRepository.GetAllAsync(ct);
        var collections = await experienceRepository.GetCollectionIdsByLibraryAsync(ct);
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return libraries.Select(library => library.ToContract(systemKeys) with
        {
            CollectionIds = collections.GetValueOrDefault(library.Id) ?? []
        }).ToList();
    }
}
