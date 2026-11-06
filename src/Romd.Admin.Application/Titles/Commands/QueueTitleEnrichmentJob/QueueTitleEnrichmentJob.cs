using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;

/// <summary>Captured retry facts; this acceptance writes no title metadata or revision.</summary>
public sealed record QueueTitleEnrichmentJobCommand(int TitleId, string TitleName, int PlatformId) : ICommand<Success>;

public sealed class QueueTitleEnrichmentJobCommandHandler(
    IEnrichmentJobAcceptance acceptance,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<QueueTitleEnrichmentJobCommandHandler> logger) : ICommandHandler<QueueTitleEnrichmentJobCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(QueueTitleEnrichmentJobCommand command, CancellationToken ct = default)
    {
        EnrichmentJob? created = null;
        bool committed = false;
        bool failed = false;
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            if (await acceptance.LockAndHasPendingForTitleStagedAsync(command.TitleId, ct))
            {
                logger.LogDebug("Enrichment job already pending for title {TitleId}", command.TitleId);
                // Successful coalescing must not commit unrelated staged state or an ambient fence touch.
                return Result.Success;
            }

            created = EnrichmentJob.Create(command.TitleName, command.TitleId, command.PlatformId, clock.GetUtcNow());
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

        logger.LogDebug("Queued enrichment job {JobId} for title {TitleId}", created!.Id, command.TitleId);
        return Result.Success;
    }
}
