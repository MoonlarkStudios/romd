using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Repository for upload job persistence.
/// </summary>
public interface IUploadJobRepository
{
    Task<IReadOnlyList<UploadJob>> GetByBatchAsync(Guid batchId, Guid? userId, CancellationToken ct = default);
    /// <summary>
    ///     Gets a job by ID.
    /// </summary>
    Task<UploadJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Gets recent jobs, optionally including archived ones.
    /// </summary>
    Task<IReadOnlyList<UploadJob>> GetRecentAsync(
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets all active (non-terminal) jobs.
    /// </summary>
    Task<IReadOnlyList<UploadJob>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    ///     Adds a new job.
    /// </summary>
    Task AddAsync(UploadJob job, CancellationToken ct = default);

    /// <summary>
    ///     Updates an existing job.
    /// </summary>
    Task UpdateAsync(UploadJob job, CancellationToken ct = default);

    /// <summary>
    ///     Permanently deletes archived jobs older than the specified age.
    /// </summary>
    /// <returns>Number of jobs deleted.</returns>
    Task<int> PurgeArchivedAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>
    ///     Archives all completed (terminal, non-archived) jobs in a single operation.
    /// </summary>
    /// <returns>Number of jobs archived.</returns>
    Task<int> ArchiveCompletedAsync(CancellationToken ct = default);

    /// <summary>
    ///     Marks stale running jobs as failed.
    ///     A job is considered stale if it has started but not been updated within the threshold.
    /// </summary>
    /// <param name="staleThreshold">How long since last update before a job is considered stale.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of jobs marked as failed.</returns>
    Task<int> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default);
}
