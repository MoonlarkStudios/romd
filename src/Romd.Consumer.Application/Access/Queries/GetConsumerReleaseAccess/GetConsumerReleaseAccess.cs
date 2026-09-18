using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Access.Queries.GetConsumerReleaseAccess;

public sealed record GetConsumerReleaseAccessQuery(int ReleaseId) : IQuery<ConsumerReleaseAccessDto>;

public sealed class GetConsumerReleaseAccessQueryHandler(
    ICurrentUser currentUser,
    IConsumerReleaseAccessRepository accessRepository,
    IServerInstanceIdentity serverInstanceIdentity)
    : IQueryHandler<GetConsumerReleaseAccessQuery, ConsumerReleaseAccessDto>
{
    public async Task<ErrorOr<ConsumerReleaseAccessDto>> HandleAsync(
        GetConsumerReleaseAccessQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var read = await accessRepository.GetAccessAsync(
            new ConsumerReleaseAccessRequest(
                new ConsumerLibraryScope(userId),
                query.ReleaseId),
            ct);

        return read switch
        {
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found
                { Value: { Allowed: true, TitleId: not null } decision } => ToContract(query.ReleaseId, decision),
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found
                { Value: { Allowed: false, TitleId: null } decision } => ToContract(query.ReleaseId, decision),
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found =>
                ConsumerErrors.CurrentLibraryUnavailable(),
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ItemNotFound =>
                ConsumerErrors.CurrentLibraryUnavailable(),
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.LibraryUnavailable =>
                ConsumerErrors.CurrentLibraryUnavailable(),
            ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent =>
                ConsumerErrors.CurrentLibraryUnavailable(),
            _ => throw new InvalidOperationException("Unknown Consumer Library access result.")
        };
    }

    private ConsumerReleaseAccessDto ToContract(int releaseId, ConsumerReleaseAccessDecision decision) =>
        new()
        {
            ServerInstanceId = serverInstanceIdentity.InstanceId.ToString("D"),
            ReleaseId = IdCoder.Encode(releaseId),
            Allowed = decision.Allowed,
            TitleId = decision.TitleId is { } titleId ? IdCoder.Encode(titleId) : null
        };
}
