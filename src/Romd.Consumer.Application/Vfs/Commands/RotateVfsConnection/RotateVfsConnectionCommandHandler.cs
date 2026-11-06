using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.RotateVfsConnection;

public sealed class RotateVfsConnectionCommandHandler : ICommandHandler<RotateVfsConnectionCommand, IssuedVfsCredential>
{
    private readonly ICurrentUser currentUser;
    private readonly IVfsConnections connections;
    private readonly IConsumerSecurityTransaction transaction;

    public RotateVfsConnectionCommandHandler(ICurrentUser currentUser, IVfsConnections connections, IConsumerSecurityTransaction transaction)
    {
        this.currentUser = currentUser;
        this.connections = connections;
        this.transaction = transaction;
    }

    public async Task<ErrorOr<IssuedVfsCredential>> HandleAsync(RotateVfsConnectionCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        return await transaction.ExecuteAsync(
            token => connections.RotateAsync(userId, command.Id, token), ct);
    }
}
