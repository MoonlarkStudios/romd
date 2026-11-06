using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;

public sealed record GetLibraryCollectionsQuery(int LibraryId, int MinimumItems, string? Cursor = null, int Limit = 50) : IQuery<Page<LibraryCollectionDto>>;
public sealed class GetLibraryCollectionsQueryHandler(ILibraryRepository repository) : IQueryHandler<GetLibraryCollectionsQuery, Page<LibraryCollectionDto>>
{
    public async Task<ErrorOr<Page<LibraryCollectionDto>>> HandleAsync(GetLibraryCollectionsQuery query, CancellationToken ct = default)
    {
        var result = await repository.GetCollectionPageAsync(query.LibraryId, query.MinimumItems, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        return new Page<LibraryCollectionDto> { Items = result.Value.Items.Select(item => item.ToContract()).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage };
    }
}
