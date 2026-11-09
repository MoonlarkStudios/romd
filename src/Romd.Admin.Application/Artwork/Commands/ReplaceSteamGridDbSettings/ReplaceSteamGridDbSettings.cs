using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork.Commands.ReplaceSteamGridDbSettings;

public sealed record ReplaceSteamGridDbSettingsCommand(UpdateSteamGridDbSettingsRequest Settings, RevisionPrecondition IfMatch)
    : ICommand<SteamGridDbSettings>
{
    public override string ToString() => nameof(ReplaceSteamGridDbSettingsCommand);
}

public sealed class ReplaceSteamGridDbSettingsCommandHandler(ISteamGridDbSettingsService service, IUnitOfWork unitOfWork,
    ILogger<ReplaceSteamGridDbSettingsCommandHandler> logger) : ICommandHandler<ReplaceSteamGridDbSettingsCommand, SteamGridDbSettings>
{
    public Task<ErrorOr<SteamGridDbSettings>> HandleAsync(ReplaceSteamGridDbSettingsCommand command, CancellationToken ct = default) =>
        unitOfWork.ExecuteInTransactionAsync(token => service.StageUpdateAsync(command.Settings, command.IfMatch, token), logger, ct);
}
