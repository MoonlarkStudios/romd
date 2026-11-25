using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.ConsumerApps.Commands.DisableConsumerWebApp;

public sealed record DisableConsumerWebAppCommand(string ClientId) : ICommand<Success>;

public sealed class DisableConsumerWebAppCommandHandler(
    IConsumerWebAppAdministration apps,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<DisableConsumerWebAppCommandHandler> logger)
    : ICommandHandler<DisableConsumerWebAppCommand, Success>
{
    public Task<ErrorOr<Success>> HandleAsync(DisableConsumerWebAppCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync<Success>(
            async token => await apps.DisableStagedAsync(command.ClientId, clock.GetUtcNow(), token)
                ? Result.Success
                : Error.NotFound("ConsumerWebApp.NotFound", "The consumer web application was not found."),
            logger, ct);
}
