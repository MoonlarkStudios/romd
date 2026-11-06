using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Commands.AccountSecurity;

public sealed record IssueAccountLinkCommand(Guid UserId, string Purpose) : ICommand<IssuedAccountLinkDto>;
public sealed class IssueAccountLinkCommandHandler(IAccountLifecycle lifecycle, IUnitOfWork unitOfWork,
    ILogger<IssueAccountLinkCommandHandler> logger) : ICommandHandler<IssueAccountLinkCommand, IssuedAccountLinkDto>
{
    public Task<ErrorOr<IssuedAccountLinkDto>> HandleAsync(IssueAccountLinkCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<IssuedAccountLinkDto>(token => lifecycle.IssueLinkAsync(command.UserId, command.Purpose, token), logger, ct);
}
