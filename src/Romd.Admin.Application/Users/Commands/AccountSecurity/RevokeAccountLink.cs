using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Commands.AccountSecurity;

public sealed record RevokeAccountLinkCommand(Guid UserId, Guid LinkId) : ICommand<Success>;
public sealed class RevokeAccountLinkCommandHandler(IAccountLifecycle lifecycle, IUnitOfWork unitOfWork,
    ILogger<RevokeAccountLinkCommandHandler> logger) : ICommandHandler<RevokeAccountLinkCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(RevokeAccountLinkCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<Success>(token => lifecycle.RevokeLinkAsync(command.UserId, command.LinkId, token), logger, ct);
}
