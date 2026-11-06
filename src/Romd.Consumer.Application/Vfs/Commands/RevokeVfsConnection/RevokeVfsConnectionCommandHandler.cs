using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.RevokeVfsConnection;

public sealed class RevokeVfsConnectionCommandHandler : ICommandHandler<RevokeVfsConnectionCommand, Success>
{
    private readonly ICurrentUser currentUser;
    private readonly IVfsConnections connections;
    private readonly IConsumerSecurityTransaction transaction;

    public RevokeVfsConnectionCommandHandler(ICurrentUser currentUser, IVfsConnections connections, IConsumerSecurityTransaction transaction)
    {
        this.currentUser = currentUser;
        this.connections = connections;
        this.transaction = transaction;
    }

    public async Task<ErrorOr<Success>> HandleAsync(RevokeVfsConnectionCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        return await transaction.ExecuteAsync(
            token => connections.RevokeAsync(userId, command.Id, token), ct);
    }
}
