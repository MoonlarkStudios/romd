using Romd.Application.Common.Readiness;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Degraded (admin host only): the worker counts as stale when the newest Hangfire
///     server heartbeat is older than <see cref="ReadinessOptions.WorkerHeartbeatStaleAfter" />.
///     Unavailable Hangfire storage degrades this signal instead of throwing.
/// </summary>
public sealed class WorkerHeartbeatReadinessCheck(
    IHangfireStorageProbe probe,
    TimeProvider timeProvider,
    ReadinessOptions options) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.Worker;

    public bool IsCritical => false;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        // The monitoring call is synchronous; yield first so the caller gets an incomplete task and
        // the evaluator's budget can bound a blocked storage read.
        await Task.Yield();

        DateTimeOffset? newestHeartbeat;
        try
        {
            newestHeartbeat = probe.GetNewestServerHeartbeatUtc();
        }
        catch (Exception)
        {
            return ReadinessCheckStatus.Degraded;
        }

        bool fresh = newestHeartbeat is { } heartbeat
            && timeProvider.GetUtcNow() - heartbeat <= options.WorkerHeartbeatStaleAfter;
        return fresh ? ReadinessCheckStatus.Healthy : ReadinessCheckStatus.Degraded;
    }
}
