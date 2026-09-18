using Hangfire;
using Microsoft.Extensions.Logging;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Realtime;

public sealed class AdminRealtimeOutboxCleanupJob(
    IAdminRealtimeOutbox outbox,
    ILogger<AdminRealtimeOutboxCleanupJob> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    [AutomaticRetry(Attempts = 1)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        int deleted = await outbox.DeleteProcessedOlderThanAsync(Retention, ct);
        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} processed admin realtime outbox event(s)", deleted);
        }
    }
}
