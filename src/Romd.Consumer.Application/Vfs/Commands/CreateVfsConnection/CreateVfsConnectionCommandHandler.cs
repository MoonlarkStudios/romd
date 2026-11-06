using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;

public sealed class CreateVfsConnectionCommandHandler : ICommandHandler<CreateVfsConnectionCommand, IssuedVfsCredential>
{
    private readonly ICurrentUser currentUser;
    private readonly IVfsConnections connections;
    private readonly IConsumerSecurityTransaction transaction;

    public CreateVfsConnectionCommandHandler(ICurrentUser currentUser, IVfsConnections connections, IConsumerSecurityTransaction transaction)
    {
        this.currentUser = currentUser;
        this.connections = connections;
        this.transaction = transaction;
    }

    public async Task<ErrorOr<IssuedVfsCredential>> HandleAsync(CreateVfsConnectionCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        if (!VfsFormats.IsValid(command.Format))
            return VfsConnectionErrors.InvalidFormat();
        if (!VfsConnectionErrors.IsValidName(command.Name))
            return VfsConnectionErrors.InvalidName();
        return await transaction.ExecuteAsync(
            token => connections.CreateAsync(userId, command.Name.Trim(), token, command.Format), ct);
    }
}
