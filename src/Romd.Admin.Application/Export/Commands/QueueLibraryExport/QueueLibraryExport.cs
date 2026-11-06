using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Export.Commands.QueueLibraryExport;

public sealed record QueueLibraryExportCommand(int? RequestedLibraryId, RomDeliveryFormat Format = RomDeliveryFormat.Original)
    : ICommand<Guid>;

public sealed class QueueLibraryExportCommandHandler(
    IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
    ICurrentUser currentUser,
    IExportJobAcceptance acceptance,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<QueueLibraryExportCommandHandler> logger) : ICommandHandler<QueueLibraryExportCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(QueueLibraryExportCommand command, CancellationToken ct = default)
    {
        if (command.Format is not (RomDeliveryFormat.Original or RomDeliveryFormat.Rvz or RomDeliveryFormat.Chd))
            return RomDeliveryErrors.InvalidFormat();

        var authorized = await scopeHandler.HandleAsync(new ResolveExportScopeQuery(command.RequestedLibraryId), ct);
        if (authorized.IsError)
            return authorized.Errors;

        // Preserve the authorized snapshot selected before waiting for the acceptance lock.
        var scope = authorized.Value.Scope;
        var actor = currentUser.UserId;
        var format = command.Format;
        var result = await unitOfWork.ExecuteInTransactionAsync<Guid>(async token =>
        {
            if (await acceptance.LockAndHasPendingStagedAsync(token))
            {
                logger.LogDebug("Export job already pending, skipping");
                return ExportErrors.AlreadyInProgress();
            }

            var job = scope switch
            {
                ExportScope.Library library => ExportJob.CreateLibrary(library.LibraryId,
                    library.MaterializationGeneration, clock.GetUtcNow(), actor, format),
                ExportScope.AllCatalog => ExportJob.CreateAllCatalog(clock.GetUtcNow(), actor, format),
                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };
            await acceptance.AddStagedAsync(job, token);
            return job.Id;
        }, logger, ct);

        if (!result.IsError)
            logger.LogInformation("Queued export job {JobId}", result.Value);
        return result;
    }
}
