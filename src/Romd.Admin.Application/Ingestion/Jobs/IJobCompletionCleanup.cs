using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Post-commit phase of the job runner. Runs only after the runner's fenced checkpoint has durably
///     persisted the completed terminal state of an attempt; never after cancellation, failure,
///     deferral, a failed save, or recovery of a journal-committed outcome. Cleanup must be idempotent
///     and leave a durable backstop (for example the orphaned-job sweep) able to finish it after a
///     crash; a failure is logged and never changes the persisted job.
/// </summary>
public interface IJobCompletionCleanup<in TJob> where TJob : Job
{
    Task RunAfterCompletionPersistedAsync(TJob job, CancellationToken ct = default);
}
