using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Unified repository for retrieving all job types.
/// </summary>
public interface IJobRepository
{
    /// <summary>Returns at most Limit + 1 jobs in stable descending creation order.</summary>
    Task<IReadOnlyList<Job>> GetHistoryAsync(JobHistoryFilter filter, CancellationToken ct = default);

    /// <summary>
    ///     Gets a job by ID (any type).
    /// </summary>
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Gets recent jobs of all types, optionally including archived ones.
    /// </summary>
    Task<IReadOnlyList<Job>> GetRecentAsync(
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets recent jobs created by a specific user, optionally including archived ones.
    /// </summary>
    Task<IReadOnlyList<Job>> GetRecentForUserAsync(
        Guid createdByUserId,
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default);

    /// <summary>
    ///     Updates an existing job.
    /// </summary>
    Task UpdateAsync(Job job, CancellationToken ct = default);

    /// <summary>
    ///     Archives all completed (terminal, non-archived) jobs in a single operation.
    /// </summary>
    /// <returns>Number of jobs archived.</returns>
    Task<int> ArchiveCompletedAsync(CancellationToken ct = default);

    /// <summary>
    ///     Permanently deletes archived jobs older than the specified age.
    /// </summary>
    /// <returns>Number of jobs deleted.</returns>
    Task<int> PurgeArchivedAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>
///     Typed repository for specific job types.
/// </summary>
public interface IJobRepository<TJob> where TJob : Job
{
    /// <summary>
    ///     Gets a job by ID.
    /// </summary>
    Task<TJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Gets all active (non-terminal) jobs.
    /// </summary>
    Task<IReadOnlyList<TJob>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    ///     Adds a new job.
    /// </summary>
    Task AddAsync(TJob job, CancellationToken ct = default);

    /// <summary>
    ///     Updates an existing job.
    /// </summary>
    Task UpdateAsync(TJob job, CancellationToken ct = default);
}

public interface IEnrichmentDispatchRepository
{
    /// <summary>
    ///     Records only the external Hangfire ID, and only while the job is still Pending,
    ///     so a stale Pending snapshot can never overwrite an already-running phase.
    ///     A no-op when the job has already started or reached a terminal phase.
    /// </summary>
    Task SetHangfireJobIdWhilePendingAsync(Guid jobId, string hangfireJobId, CancellationToken ct = default);
}

/// <summary>
///     Repository capability for job types that require a database-enforced,
///     cross-worker transition from Pending into active execution.
/// </summary>
public interface IJobExecutionClaimRepository<TJob> : IJobRepository<TJob> where TJob : Job
{
    /// <summary>
    ///     Atomically claims a Pending job for the external delivery, or reclaims an
    ///     abandoned active claim for that same delivery after its lease expires. Returns
    ///     the active job to the winner, or <see langword="null"/> while another worker owns
    ///     the lease or a distinct delivery owns the job.
    /// </summary>
    Task<JobExecutionClaim<TJob>?> TryClaimExecutionAsync(
        Guid jobId,
        string deliveryId,
        CancellationToken ct = default);

    Task<bool> RenewExecutionLeaseAsync(
        Guid jobId,
        Guid fenceToken,
        CancellationToken ct = default);

    Task<bool> HasExecutionOwnershipAsync(
        Guid jobId,
        Guid fenceToken,
        CancellationToken ct = default);

    Task<bool> TryUpdateClaimedAsync(
        TJob job,
        Guid fenceToken,
        CancellationToken ct = default);
}

public sealed record JobExecutionClaim<TJob>(TJob Job, Guid FenceToken) where TJob : Job;

public sealed class JobExecutionClaimOptions
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);
}

public interface IEnrichmentJobRepository
    : IJobExecutionClaimRepository<EnrichmentJob>, IEnrichmentDispatchRepository
{
    /// <summary>
    ///     Gets the earliest active (non-terminal) enrichment job for a title, if any.
    /// </summary>
    Task<EnrichmentJob?> GetActiveForTitleAsync(int titleId, CancellationToken ct = default);

    /// <summary>
    ///     Stages an enrichment job without saving. The caller owns the flush and commit.
    /// </summary>
    Task AddStagedAsync(EnrichmentJob job, CancellationToken ct = default);
}

public interface IBulkEnrichmentJobRepository
    : IJobExecutionClaimRepository<BulkEnrichmentJob>, IEnrichmentDispatchRepository
{
    /// <summary>
    ///     Checks whether the platform already has a pending or active bulk enrichment job.
    /// </summary>
    Task<bool> HasPendingForPlatformAsync(int platformId, CancellationToken ct = default);
}

public interface IReplaceDatJobRepository : IJobRepository<ReplaceDatJob>
{
    /// <summary>
    ///     Gets started, non-terminal replace jobs whose last persisted progress update
    ///     (checkpoint) is older than the stale threshold. Jobs with recent progress,
    ///     terminal jobs, and jobs that never started are never returned.
    /// </summary>
    Task<IReadOnlyList<ReplaceDatJob>> GetStaleStartedAsync(
        TimeSpan staleThreshold,
        CancellationToken ct = default);
}

public interface IMaterializationJobRepository : IJobRepository<MaterializationJob>
{
    Task<MaterializationJob?> GetActiveForLibraryAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Stages a materialization job without saving. The caller owns the flush and commit.
    /// </summary>
    Task AddStagedAsync(MaterializationJob job, CancellationToken ct = default);

    Task<bool> TryAddIfNoActiveForLibraryAsync(MaterializationJob job, CancellationToken ct = default);

    /// <summary>
    ///     Records only the external Hangfire ID without replacing job phase or progress state.
    /// </summary>
    Task SetHangfireJobIdAsync(Guid jobId, string hangfireJobId, CancellationToken ct = default);
}
