using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;

public sealed record CancelJobCommand(Guid JobId) : ICommand<Success>;

public sealed class CancelJobCommandHandler(
    IJobRepository jobs,
    IJobCancellationStore cancellations,
    IMediaReconciliationRequests mediaReconciliation,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    TimeProvider clock,
    IJobDeliveryCancellation delivery,
    ILogger<CancelJobCommandHandler> logger) : ICommandHandler<CancelJobCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(CancelJobCommand command, CancellationToken ct = default)
    {
        var visibility = JobVisibility.ScopeFor(user);
        var observed = visibility.Empty ? null : await jobs.GetByIdAsync(command.JobId, ct);
        if (observed is null || !JobVisibility.IsVisibleTo(observed, visibility))
            return JobErrors.NotFound(command.JobId);
        if (observed is MetadataRematerializationJob)
            return JobErrors.NotCancellable();
        if (observed.IsTerminal)
            return JobErrors.AlreadyComplete();

        // Authorization accepted the stop request. Its durable boundary no longer depends on
        // the HTTP request lifetime, including a client disconnect while waiting for the row.
        var result = await unitOfWork.ExecuteInTransactionAsync<CancellationReceipt>(async token =>
        {
            if (observed is MediaReconciliationJob)
            {
                // This store already locks and revalidates its current durable journal. It records
                // stop intent only; the worker retains custody and independently settles the outcome.
                var requested = await mediaReconciliation.StageCancellationAsync(command.JobId, token);
                return requested.IsError ? requested.Errors : new CancellationReceipt(null);
            }

            var current = await cancellations.LockVisibleForCancellationStagedAsync(command.JobId, visibility, token);
            if (current is null)
                return JobErrors.NotFound(command.JobId);
            if (current is MetadataRematerializationJob)
                return JobErrors.NotCancellable();
            if (current.IsTerminal)
                return JobErrors.AlreadyComplete();

            if (current is not CancellableJob cancellable)
                throw new InvalidOperationException("This job does not support direct cancellation.");
            cancellable.Cancel(clock.GetUtcNow());
            await cancellations.ApplyCancellationStagedAsync(current, token);
            return new CancellationReceipt(current.HangfireJobId);
        }, logger, CancellationToken.None);
        if (result.IsError)
            return result.Errors;

        if (!string.IsNullOrEmpty(result.Value.DeliveryId))
        {
            try
            {
                await delivery.RequestStopAsync(result.Value.DeliveryId, CancellationToken.None);
            }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "Job {JobId} was durably cancelled but delivery {DeliveryId} could not be stopped",
                    command.JobId, result.Value.DeliveryId);
            }
        }
        return Result.Success;
    }

    private sealed record CancellationReceipt(string? DeliveryId);
}
