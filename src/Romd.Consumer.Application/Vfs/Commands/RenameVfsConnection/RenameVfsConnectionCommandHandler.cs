using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Vfs.Commands.RenameVfsConnection;

public sealed class RenameVfsConnectionCommandHandler : ICommandHandler<RenameVfsConnectionCommand, VfsConnectionDto>
{
    private readonly ICurrentUser currentUser;
    private readonly IVfsConnections connections;

    public RenameVfsConnectionCommandHandler(ICurrentUser currentUser, IVfsConnections connections)
    {
        this.currentUser = currentUser;
        this.connections = connections;
    }

    public async Task<ErrorOr<VfsConnectionDto>> HandleAsync(RenameVfsConnectionCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        if (command.Format is { } format && !VfsFormats.IsValid(format))
            return VfsConnectionErrors.InvalidFormat();
        if (command.Name is not null && !VfsConnectionErrors.IsValidName(command.Name))
            return VfsConnectionErrors.InvalidName();
        return await connections.RenameAsync(userId, command.Id, command.Name?.Trim(), ct, command.Format);
    }
}
