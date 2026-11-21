using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryById;

public sealed record GetLibraryByIdQuery(int LibraryId) : IQuery<Library>;

public sealed class GetLibraryByIdQueryHandler(IReferenceCatalogService referenceCatalog, ILibraryRepository libraryRepository,
    ILibraryExperienceRepository experienceRepository, RomDeliveryPolicy deliveryPolicy)
    : IQueryHandler<GetLibraryByIdQuery, Library>
{
    public async Task<ErrorOr<Library>> HandleAsync(
        GetLibraryByIdQuery query,
        CancellationToken ct = default)
    {
        var library = await libraryRepository.GetByIdAsync(query.LibraryId, ct);
        if (library is null)
        {
            return LibraryErrors.NotFound();
        }

        int? gameCount = library.HasValidConfiguration && library.LastMaterializedAt.HasValue
            ? (await experienceRepository.GetPublishedGameCountsAsync([library.Id], ct)).GetValueOrDefault(library.Id)
            : null;
        return library.ToContract(await referenceCatalog.GetSystemKeysAsync(ct), deliveryPolicy, gameCount);
    }
}
