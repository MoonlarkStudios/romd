using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>The runner never infers terminal reconciliation from a returned media call or releases its sources itself.</summary>
public sealed class MediaReconciliationLifecyclePolicy : IJobCancellationPolicy<MediaReconciliationJob>, IJobTerminationPolicy<MediaReconciliationJob>
{
    private readonly IMediaReconciliationExecution _execution;
    private readonly IMediaReconciliationSelections _selections;
    public MediaReconciliationLifecyclePolicy(IMediaReconciliationExecution execution, IMediaReconciliationSelections selections)
    {
        this._execution = execution;
        this._selections = selections;
    }
    public async Task<JobCancellationSignal> ReadSignalAsync(Guid jobId, CancellationToken ct = default) =>
        await _execution.IsCancellationRequestedAsync(jobId, ct) ? JobCancellationSignal.Requested : JobCancellationSignal.NotRequested;
    public async Task<bool> TryRecoverCommittedOutcomeAsync(MediaReconciliationJob job, CancellationToken ct = default)
    {
        var outcome = await _execution.ReadOutcomeAsync(job.Id, ct);
        if (outcome is null)
            return false;
        if (outcome.JobId != job.Id)
            throw new InvalidDataException("Reconciliation journal belongs to another job.");
        if (!job.IsTerminal && job.Request is MediaReconciliationRequest.SelectionWork)
        {
            var state = await _selections.ReadAsync(job.Id, ct) ?? throw new InvalidDataException("The terminal selection lost its durable progress.");
            job.ApplySelectionProgress(state.Progress);
        }
        job.ApplyOutcome(outcome.Kind, outcome.Code, outcome.CompletedAt);
        return true;
    }
    public async Task<JobTermination> ReconcileAsync(MediaReconciliationJob job, JobTerminationCause cause, CancellationToken ct = default)
    {
        if (await TryRecoverCommittedOutcomeAsync(job, ct))
            return JobTermination.Settled;
        var kind = cause == JobTerminationCause.Cancelled ? MediaReconciliationOutcomeKind.Cancelled : MediaReconciliationOutcomeKind.Failed;
        string code = cause == JobTerminationCause.Cancelled ? "cancelled" : "retry-budget-exhausted";
        ErrorOr<MediaReconciliationJournalOutcome> result;
        try
        {
            if (job.Request is MediaReconciliationRequest.SelectionWork)
            {
                var stopped = await _selections.StopOwnedAsync(job.Id, ct);
                if (stopped.IsError)
                    return JobTermination.Defer("Reconciliation selection ownership remains unresolved.");
                job.ApplySelectionProgress(stopped.Value.Progress);
                result = await _selections.FinishAsync(job.Id, kind, code, ct);
            }
            else
            {
                var demand = await _execution.ReadDemandAsync(job.Id, ct);
                if (demand is null)
                    return await TryRecoverCommittedOutcomeAsync(job, ct) ? JobTermination.Settled : JobTermination.Defer("Reconciliation demand changed during termination.");
                result = await _execution.FinishAsync(job.Id, demand.Version, kind, code, processTreeStopped: true, ct);
            }
        }
        catch { if (await TryRecoverCommittedOutcomeAsync(job, CancellationToken.None)) return JobTermination.Settled; throw; }
        if (result.IsError)
            return JobTermination.Defer("Reconciliation process or originally owned child custody remains unresolved.");
        if (job.Request is MediaReconciliationRequest.SelectionWork)
        {
            var state = await _selections.ReadAsync(job.Id, ct) ?? throw new InvalidDataException("Selection completion lost its durable progress.");
            job.ApplySelectionProgress(state.Progress);
        }
        var outcome = result.Value;
        if (outcome.JobId != job.Id)
            throw new InvalidDataException("Reconciliation journal belongs to another job.");
        job.ApplyOutcome(outcome.Kind, outcome.Code, outcome.CompletedAt);
        return JobTermination.Settled;
    }
}
