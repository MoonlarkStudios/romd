using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Browse;

namespace Romd.Consumer.Application.Browse.Queries.GetConsumerPlatform;

public sealed record GetConsumerPlatformQuery(int PlatformId) : IQuery<ConsumerPlatformDetailDto>;

public sealed class GetConsumerPlatformQueryHandler(
    ICurrentUser currentUser,
    IConsumerBrowseRepository browseRepository)
    : IQueryHandler<GetConsumerPlatformQuery, ConsumerPlatformDetailDto>
{
    public async Task<ErrorOr<ConsumerPlatformDetailDto>> HandleAsync(
        GetConsumerPlatformQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var result = await browseRepository.GetPlatformAsync(
            new ConsumerLibraryScope(userId),
            query.PlatformId,
            ct);

        return result switch
        {
            ConsumerLibraryReadResult<ConsumerPlatformDetailData>.Found found => found.Value.ToContract(),
            ConsumerLibraryReadResult<ConsumerPlatformDetailData>.ItemNotFound =>
                ConsumerErrors.PlatformNotFound(),
            ConsumerLibraryReadResult<ConsumerPlatformDetailData>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerPlatformDetailData>.ProjectionInconsistent =>
                ConsumerErrors.PlatformNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library platform-detail result.")
        };
    }
}
