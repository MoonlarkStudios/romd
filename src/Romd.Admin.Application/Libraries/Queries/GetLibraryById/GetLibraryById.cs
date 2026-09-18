using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryById;

public sealed record GetLibraryByIdQuery(int LibraryId) : IQuery<LibraryDto>;

public sealed class GetLibraryByIdQueryHandler(IReferenceCatalogService referenceCatalog, ILibraryRepository libraryRepository)
    : IQueryHandler<GetLibraryByIdQuery, LibraryDto>
{
    public async Task<ErrorOr<LibraryDto>> HandleAsync(
        GetLibraryByIdQuery query,
        CancellationToken ct = default)
    {
        var library = await libraryRepository.GetByIdAsync(query.LibraryId, ct);
        return library is null ? LibraryErrors.NotFound() : library.ToContract(await referenceCatalog.GetSystemKeysAsync(ct));
    }
}
