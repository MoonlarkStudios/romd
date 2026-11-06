using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Commands.AccountSecurity;

public sealed record SetAccountSuspensionCommand(Guid UserId, bool Suspended) : ICommand<Success>;
public sealed class SetAccountSuspensionCommandHandler(IAccountLifecycle lifecycle, IUnitOfWork unitOfWork,
    ILogger<SetAccountSuspensionCommandHandler> logger) : ICommandHandler<SetAccountSuspensionCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(SetAccountSuspensionCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<Success>(token => lifecycle.SetSuspensionAsync(command.UserId, command.Suspended, token), logger, ct);
}
