using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Repository for upload job persistence.
/// </summary>
public interface IUploadJobRepository
{
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
    ///     Stages an existing job checkpoint inside the required caller-owned transaction.
    ///     The caller owns the flush and commit.
    /// </summary>
    Task UpdateStagedAsync(UploadJob job, CancellationToken ct = default);

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
    ///     Marks legacy, unclaimed running jobs without a durable dispatch or deferral as failed
    ///     when they have not been updated within the threshold. Durable work uses lease recovery.
    /// </summary>
    /// <param name="staleThreshold">How long since last update before a job is considered stale.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of jobs marked as failed.</returns>
    Task<int> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default);
}
