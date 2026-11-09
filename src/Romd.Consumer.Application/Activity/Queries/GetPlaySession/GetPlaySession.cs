using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Activity;

namespace Romd.Consumer.Application.Activity.Queries.GetPlaySession;

public sealed record GetPlaySessionQuery(Guid SessionId) : IQuery<PlaySession>;

public sealed class GetPlaySessionQueryHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : IQueryHandler<GetPlaySessionQuery, PlaySession>
{
    public async Task<ErrorOr<PlaySession>> HandleAsync(GetPlaySessionQuery query, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var session = await repository.GetAccessibleAsync(userId, query.SessionId, ct);
        return session is null ? ConsumerErrors.PlaySessionNotFound() : session.ToContract();
    }
}
