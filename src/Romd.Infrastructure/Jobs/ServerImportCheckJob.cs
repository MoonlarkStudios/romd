using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import.Commands.StartServerImport;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;

namespace Romd.Infrastructure.Jobs;

public sealed class ServerImportCheckJob(
    IServerImportOptions options, ICommandHandler<StartServerImportCommand, ServerImportResult> handler,
    ILogger<ServerImportCheckJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        // Pending Hangfire deliveries may outlive a deployment that removed the schedule.
        if (options.ServerImportDirectory is null || options.ServerImportIntervalMinutes is null)
            return;

        var result = await handler.HandleAsync(new(ActorId: null), ct);
        if (result.IsError)
            logger.LogWarning("Scheduled server import was not checked: {Reason}", result.FirstError.Description);
    }
}
