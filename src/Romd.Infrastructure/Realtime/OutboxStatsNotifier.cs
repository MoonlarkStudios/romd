using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Dashboard;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Realtime;

public sealed class OutboxStatsNotifier(
    IAdminEventOutbox outbox,
    RomdDbContext context,
    AdminRealtimeOutboxNotifierCommitGate commitGate,
    ILogger<OutboxStatsNotifier> logger) : IStatsNotifier
{
    public Task NotifyStorageChangedAsync(CancellationToken ct = default) =>
        EnqueueAndCommitAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);

    public Task NotifyCoverageChangedAsync(CancellationToken ct = default) =>
        EnqueueAndCommitAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);

    public Task NotifyHealthChangedAsync(CancellationToken ct = default) =>
        EnqueueAndCommitAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);

    private Task EnqueueAndCommitAsync(string eventType, CancellationToken ct) =>
        commitGate.RunNotifierCommitAsync(async () =>
        {
            var previouslyTrackedEvents = new HashSet<AdminRealtimeOutboxEventEntity>(
                context.ChangeTracker
                    .Entries<AdminRealtimeOutboxEventEntity>()
                    .Select(entry => entry.Entity),
                ReferenceEqualityComparer.Instance);

            try
            {
                await outbox.EnqueueAsync(eventType, ct);

                // Notifier ports are standalone best-effort publication boundaries,
                // so they persist their own outbox intent. Transactional mutation
                // handlers instead enlist IAdminEventOutbox in their owning unit of work.
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                foreach (var entry in context.ChangeTracker.Entries<AdminRealtimeOutboxEventEntity>()
                             .Where(entry => !previouslyTrackedEvents.Contains(entry.Entity)))
                {
                    entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                }

                logger.LogWarning(ex, "Failed to enqueue admin realtime {EventType} stats change", eventType);
            }
        }, ct);
}
