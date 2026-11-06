using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.ListLibraries;

public sealed record ListLibrariesQuery(string? Query = null, string? Cursor = null, int Limit = 50) : IQuery<Page<LibraryDto>>;

public sealed class ListLibrariesQueryHandler(IReferenceCatalogService referenceCatalog, ILibraryRepository libraryRepository, ILibraryExperienceRepository experienceRepository,
    RomDeliveryPolicy deliveryPolicy)
    : IQueryHandler<ListLibrariesQuery, Page<LibraryDto>>
{
    public async Task<ErrorOr<Page<LibraryDto>>> HandleAsync(
        ListLibrariesQuery query,
        CancellationToken ct = default)
    {
        var result = await libraryRepository.GetPageAsync(query.Query, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        var libraries = result.Value.Items;
        var collections = await experienceRepository.GetCollectionIdsByLibraryAsync(ct, libraries.Select(l => l.Id).ToArray());
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return new Page<LibraryDto>
        {
            Items = libraries.Select(library => library.ToContract(systemKeys, deliveryPolicy) with
            {
                CollectionIds = collections.GetValueOrDefault(library.Id) ?? []
            }).ToArray(),
            NextCursor = result.Value.NextCursor,
            HasNextPage = result.Value.HasNextPage
        };
    }
}
