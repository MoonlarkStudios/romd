using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.ConsumerApps.Commands.DisableConsumerWebApp;

public sealed record DisableConsumerWebAppCommand(string ClientId) : ICommand<Success>;

public sealed class DisableConsumerWebAppCommandHandler(IConsumerWebAppAdministration apps, TimeProvider clock)
    : ICommandHandler<DisableConsumerWebAppCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(DisableConsumerWebAppCommand command, CancellationToken ct = default) =>
        await apps.DisableAsync(command.ClientId, clock.GetUtcNow(), ct)
            ? Result.Success
            : Error.NotFound("ConsumerWebApp.NotFound", "The consumer web application was not found.");
}
