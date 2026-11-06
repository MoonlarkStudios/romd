using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>Current-state cancellation inside the caller's ReadCommitted transaction.</summary>
public interface IJobCancellationStore
{
    Task<Job?> LockVisibleForCancellationStagedAsync(
        Guid jobId,
        JobVisibilityScope visibility,
        CancellationToken ct = default);

    /// <summary>
    ///     Applies cancellation to the same locked job returned by this store. Only cancellation,
    ///     execution ownership and the matching artwork pending intent may change. Caller commits.
    /// </summary>
    Task ApplyCancellationStagedAsync(Job job, CancellationToken ct = default);
}

/// <summary>Best-effort transport signal after authoritative cancellation has committed.</summary>
public interface IJobDeliveryCancellation
{
    Task RequestStopAsync(string deliveryId, CancellationToken ct = default);
}
