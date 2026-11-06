using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Commands.AccountSecurity;

public sealed record RevokeAccountSessionsCommand(Guid UserId, string? SessionId, Guid? ExceptSessionId = null) : ICommand<Success>;
public sealed class RevokeAccountSessionsCommandHandler(IAccountLifecycle lifecycle, IUnitOfWork unitOfWork,
    ILogger<RevokeAccountSessionsCommandHandler> logger) : ICommandHandler<RevokeAccountSessionsCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(RevokeAccountSessionsCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<Success>(token => lifecycle.RevokeSessionsAsync(command.UserId, command.SessionId, token, command.ExceptSessionId), logger, ct);
}
