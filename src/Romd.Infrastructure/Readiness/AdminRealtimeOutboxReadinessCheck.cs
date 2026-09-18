using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Readiness;
using Romd.Persistence;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Degraded (admin host only): the admin realtime outbox counts as stalled when any
///     unprocessed row is older than <see cref="ReadinessOptions.OutboxStalledAfter" />. Read-only
///     existence query; dispatch and cleanup stay with their owners.
/// </summary>
public sealed class AdminRealtimeOutboxReadinessCheck(
    RomdDbContext context,
    TimeProvider timeProvider,
    ReadinessOptions options) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.Outbox;

    public bool IsCritical => false;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        var stalledBefore = timeProvider.GetUtcNow() - options.OutboxStalledAfter;
        bool stalled = await context.AdminRealtimeOutboxEvents
            .AnyAsync(
                row => row.ProcessedAtUtc == null && row.CreatedAtUtc < stalledBefore,
                cancellationToken);

        return stalled ? ReadinessCheckStatus.Degraded : ReadinessCheckStatus.Healthy;
    }
}
