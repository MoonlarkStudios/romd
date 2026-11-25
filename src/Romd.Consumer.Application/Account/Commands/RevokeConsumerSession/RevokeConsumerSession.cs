using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.Account.Commands.RevokeConsumerSession;

public sealed record RevokeConsumerSessionCommand(Guid SessionId) : ICommand<Success>;

public sealed class RevokeConsumerSessionCommandHandler(ICurrentUser user, IAccountSessions sessions)
    : ICommandHandler<RevokeConsumerSessionCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(RevokeConsumerSessionCommand command, CancellationToken ct = default)
    {
        if (user.UserId is not { } id)
            return ConsumerErrors.CurrentUserRequired();

        return await sessions.RevokeAsync(id, command.SessionId, null, id, "Revoked from account", ct)
            ? Result.Success
            : Error.NotFound("Account.SessionNotFound", "The session was not found.");
    }
}
