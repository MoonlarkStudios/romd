using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections.Queries.GetConsumerCollection;

public sealed record GetConsumerCollectionQuery(int CollectionId) : IQuery<ConsumerCollectionDto>;

public sealed class GetConsumerCollectionQueryHandler(
    ICurrentUser currentUser,
    IConsumerCollectionReadRepository collectionRepository)
    : IQueryHandler<GetConsumerCollectionQuery, ConsumerCollectionDto>
{
    public async Task<ErrorOr<ConsumerCollectionDto>> HandleAsync(
        GetConsumerCollectionQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var collection = await collectionRepository.GetCollectionByIdAsync(
            new ConsumerLibraryScope(userId),
            query.CollectionId,
            ct);

        return collection switch
        {
            ConsumerLibraryReadResult<ConsumerCollectionReadModel>.Found found => found.Value.ToDto(),
            ConsumerLibraryReadResult<ConsumerCollectionReadModel>.ItemNotFound =>
                ConsumerErrors.CollectionNotFound(),
            ConsumerLibraryReadResult<ConsumerCollectionReadModel>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerCollectionReadModel>.ProjectionInconsistent =>
                ConsumerErrors.CollectionNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library collection-detail result.")
        };
    }
}
