using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections.Queries.ListConsumerCollections;

public sealed record ListConsumerCollectionsQuery(int? PlatformId = null, string? Cursor = null, int Limit = 50)
    : IQuery<Page<ConsumerCollection>>
{
    public const int MaxLimit = 100;
}

public sealed class ListConsumerCollectionsQueryHandler(
    ICurrentUser currentUser,
    IConsumerCollectionReadRepository collectionRepository)
    : IQueryHandler<ListConsumerCollectionsQuery, Page<ConsumerCollection>>
{
    public async Task<ErrorOr<Page<ConsumerCollection>>> HandleAsync(
        ListConsumerCollectionsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var collections = await collectionRepository.GetCollectionsPageAsync(
            new ConsumerLibraryScope(userId),
            query.PlatformId,
            query.Cursor,
            query.Limit,
            ct);

        return collections switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.Found found =>
                new Page<ConsumerCollection> { Items = found.Value.Items.Select(collection => collection.ToDto()).ToArray(), NextCursor = found.Value.NextCursor, HasNextPage = found.Value.HasNextPage },
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.Rejected rejected => rejected.Error,
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.ItemNotFound =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>.ProjectionInconsistent =>
                ConsumerErrors.LibraryNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library collection-list result.")
        };
    }
}
