namespace Romd.Application.Common.Readiness;

/// <summary>
///     Central readiness thresholds and budgets. The defaults are the production values; there is
///     deliberately no configuration binding. Tests may construct instances with shorter budgets.
/// </summary>
public sealed record ReadinessOptions
{
    /// <summary>
    ///     Per-check evaluation budget. A check that exceeds it counts as failed: unready for a
    ///     critical check, degraded for a non-critical one. No in-check retries.
    /// </summary>
    public TimeSpan CheckBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Overall budget for one full evaluation. Once exhausted, remaining checks are not invoked
    ///     and every uninvoked or unfinished check counts as failed per its criticality.
    /// </summary>
    public TimeSpan EvaluationBudget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long one evaluation result is reused before the checks run again. Concurrent probes
    ///     within the window share a single evaluation.
    /// </summary>
    public TimeSpan ResultCacheTtl { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Age of the oldest unprocessed admin realtime outbox row before the outbox counts as
    ///     stalled (degraded).
    /// </summary>
    public TimeSpan OutboxStalledAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Maximum age of the newest Hangfire server heartbeat before the worker counts as stale
    ///     (degraded).
    /// </summary>
    public TimeSpan WorkerHeartbeatStaleAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     Minimum free space on the volume containing <c>Romd:DataDirectory</c> before disk counts
    ///     as degraded. 1 GiB.
    /// </summary>
    public long MinimumFreeDiskBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}
