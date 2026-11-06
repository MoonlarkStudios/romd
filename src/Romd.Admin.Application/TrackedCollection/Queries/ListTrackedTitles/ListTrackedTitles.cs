using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.ListTrackedTitles;

public sealed record ListTrackedTitlesQuery(TrackedCollectionView View, string? Query, string? Cursor, int Limit) : IQuery<Page<TrackedCollectionTitleDto>>;
public sealed class ListTrackedTitlesQueryHandler(ITrackedCollectionReadRepository repository, IReferenceCatalogService referenceCatalog) : IQueryHandler<ListTrackedTitlesQuery, Page<TrackedCollectionTitleDto>>
{
    public async Task<ErrorOr<Page<TrackedCollectionTitleDto>>> HandleAsync(ListTrackedTitlesQuery query, CancellationToken ct = default)
    {
        var result = await repository.ListPageAsync(query.View, query.Query, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        var keys = await referenceCatalog.GetSystemKeysAsync(ct);
        return new Page<TrackedCollectionTitleDto> { Items = result.Value.Items.Select(item => TrackedCollectionContractMapper.ToContract(item, keys)).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage };
    }
}
