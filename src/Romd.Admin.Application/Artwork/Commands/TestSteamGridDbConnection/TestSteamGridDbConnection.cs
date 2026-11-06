using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork.Commands.TestSteamGridDbConnection;

public sealed record TestSteamGridDbConnectionCommand(RevisionPrecondition IfMatch) : ICommand<SteamGridDbSettingsDto>;

public sealed class TestSteamGridDbConnectionCommandHandler(ISteamGridDbSettingsService service, IUnitOfWork unitOfWork,
    ILogger<TestSteamGridDbConnectionCommandHandler> logger) : ICommandHandler<TestSteamGridDbConnectionCommand, SteamGridDbSettingsDto>
{
    public async Task<ErrorOr<SteamGridDbSettingsDto>> HandleAsync(TestSteamGridDbConnectionCommand command, CancellationToken ct = default)
    {
        // A probe can take twenty seconds. Only its final CAS belongs in the short owned transaction.
        var probe = await service.ProbeAsync(command.IfMatch, ct);
        if (probe.IsError)
            return probe.Errors;
        return await unitOfWork.ExecuteInTransactionAsync(token => service.StageTestAsync(probe.Value, token), logger, ct);
    }
}
