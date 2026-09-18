using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users.Commands.AccountSecurity;

public sealed record RedeemAccountLinkCommand(string Token, string Password) : ICommand<Success>
{
    public override string ToString() => nameof(RedeemAccountLinkCommand);
}
public sealed class RedeemAccountLinkCommandHandler(IAccountLifecycle lifecycle, IUnitOfWork unitOfWork,
    ILogger<RedeemAccountLinkCommandHandler> logger) : ICommandHandler<RedeemAccountLinkCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(RedeemAccountLinkCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<Success>(token => lifecycle.RedeemLinkAsync(command.Token, command.Password, token), logger, ct);
}
