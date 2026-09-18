using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections.Queries.ListConsumerCollections;

public sealed record ListConsumerCollectionsQuery(int? PlatformId = null) : IQuery<IReadOnlyList<ConsumerCollectionDto>>;

public sealed class ListConsumerCollectionsQueryHandler(
    ICurrentUser currentUser,
    IConsumerCollectionReadRepository collectionRepository)
    : IQueryHandler<ListConsumerCollectionsQuery, IReadOnlyList<ConsumerCollectionDto>>
{
    public async Task<ErrorOr<IReadOnlyList<ConsumerCollectionDto>>> HandleAsync(
        ListConsumerCollectionsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var collections = await collectionRepository.GetCollectionsAsync(
            new ConsumerLibraryScope(userId),
            query.PlatformId,
            ct);

        return collections switch
        {
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>.Found found =>
                found.Value.Select(collection => collection.ToDto()).ToList(),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>.ItemNotFound =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>.ProjectionInconsistent =>
                ConsumerErrors.LibraryNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library collection-list result.")
        };
    }
}
