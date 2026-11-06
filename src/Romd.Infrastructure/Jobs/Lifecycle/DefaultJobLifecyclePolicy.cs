using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>
///     Lifecycle of an ordinary job: no deferral budget, no durable cancellation request, no family
///     journal or custody, and no post-commit cleanup. Registered as the open-generic fallback for every
///     lifecycle capability; a family overrides only the capabilities it needs with closed registrations.
/// </summary>
public sealed class DefaultJobLifecyclePolicy<TJob> :
    IJobRetryBudgetPolicy<TJob>,
    IJobCancellationPolicy<TJob>,
    IJobTerminationPolicy<TJob>,
    IJobCompletionCleanup<TJob>
    where TJob : Job
{
    public JobRetryBudget? BudgetFor(TJob job) => null;

    public Task<JobCancellationSignal> ReadSignalAsync(Guid jobId, CancellationToken ct = default) =>
        Task.FromResult(JobCancellationSignal.Untracked);

    public Task<bool> TryRecoverCommittedOutcomeAsync(TJob job, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<JobTermination> ReconcileAsync(TJob job, JobTerminationCause cause, CancellationToken ct = default) =>
        Task.FromResult(JobTermination.Transition);

    public Task RunAfterCompletionPersistedAsync(TJob job, CancellationToken ct = default) => Task.CompletedTask;
}
