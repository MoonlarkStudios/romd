using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;

/// <summary>Immutable request facts shared by HTTP acceptance and the upload's prepared best-effort follow-up.</summary>
public sealed record QueueBulkEnrichmentJobCommand(int PlatformId, EnrichmentScope Scope = EnrichmentScope.Tracked,
    Guid? CreatedByUserId = null) : ICommand<Guid>;

public sealed class QueueBulkEnrichmentJobCommandHandler(
    IBulkEnrichmentJobAcceptance acceptance,
    IPlatformRepository platforms,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<QueueBulkEnrichmentJobCommandHandler> logger) : ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(QueueBulkEnrichmentJobCommand command, CancellationToken ct = default)
    {
        BulkEnrichmentJob? created = null;
        bool committed = false;
        bool failed = false;
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            if (await acceptance.LockAndGetPendingForPlatformStagedAsync(command.PlatformId, ct) is { } existingId)
            {
                logger.LogDebug("Bulk enrichment job already pending for platform {PlatformId}", command.PlatformId);
                // Reuse preserves the existing scope/creator and rolls back this no-op transaction.
                return existingId;
            }

            var platform = await platforms.GetByIdAsync(command.PlatformId, ct);
            created = BulkEnrichmentJob.Create(command.PlatformId, platform?.ShortName ?? string.Empty,
                clock.GetUtcNow(), command.Scope, command.CreatedByUserId);
            await acceptance.AddStagedAsync(created, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await transaction.DisposePreservingOutcomeAsync(committed, failed, logger);
        }

        var acceptedId = created!.Id;
        logger.LogInformation("Queued bulk enrichment job {JobId} for platform {PlatformId}", acceptedId, command.PlatformId);
        return acceptedId;
    }
}
