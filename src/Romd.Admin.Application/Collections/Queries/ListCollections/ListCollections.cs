using Romd.Contracts.Common.Models;
using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Queries.ListCollections;

public sealed record ListCollectionsQuery(int? PlatformId = null, string? Query = null, string? Cursor = null, int Limit = 50)
    : IQuery<Page<CollectionSummary>>;

public sealed class ListCollectionsQueryHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : IQueryHandler<ListCollectionsQuery, Page<CollectionSummary>>
{
    public async Task<ErrorOr<Page<CollectionSummary>>> HandleAsync(
        ListCollectionsQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var result = await collectionRepository.GetPageAsync(query.PlatformId, query.Query, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        return new Page<CollectionSummary> { Items = result.Value.Items.Select(m => m.ToSummary(systemKeys)).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage };
    }
}
