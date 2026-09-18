using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Realtime;

public sealed class OutboxJobNotifier(
    IAdminEventOutbox outbox,
    RomdDbContext context,
    AdminRealtimeOutboxNotifierCommitGate commitGate,
    ILogger<OutboxJobNotifier> logger) : IJobNotifier
{
    public Task NotifyJobUpdatedAsync(Job job, CancellationToken ct = default) =>
        commitGate.RunNotifierCommitAsync(async () =>
        {
            JobDto payload = job.ToContract(await new Romd.Persistence.Queries.SystemSummaryReader(context).ReadKeysAsync(ct));
            var previouslyTrackedEvents = CaptureTrackedEvents();

            try
            {
                await outbox.EnqueueAsync(AdminRealtimeEventTypes.JobUpdated, payload, ct);

                // Notifier ports are standalone best-effort publication boundaries,
                // so they persist their own outbox intent. Transactional mutation
                // handlers instead enlist IAdminEventOutbox in their owning unit of work.
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                DetachNewlyAddedEvents(previouslyTrackedEvents);
                logger.LogWarning(ex, "Failed to enqueue admin realtime job update for job {JobId}", job.Id);
            }
        }, ct);

    public Task NotifyTitleEnrichedAsync(
        int titleId,
        int platformId,
        int? coverMediaId = null,
        CancellationToken ct = default) =>
        commitGate.RunNotifierCommitAsync(async () =>
        {
            var previouslyTrackedEvents = CaptureTrackedEvents();

            try
            {
                await outbox.EnqueueAsync(
                    AdminRealtimeEventTypes.TitleEnriched,
                    new AdminRealtimeTitleEnrichedOutboxPayload(titleId, platformId, coverMediaId),
                    ct);

                // Notifier ports are standalone best-effort publication boundaries,
                // so they persist their own outbox intent. Transactional mutation
                // handlers instead enlist IAdminEventOutbox in their owning unit of work.
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                DetachNewlyAddedEvents(previouslyTrackedEvents);
                logger.LogWarning(ex, "Failed to enqueue admin realtime title enrichment for title {TitleId}", titleId);
            }
        }, ct);

    private HashSet<AdminRealtimeOutboxEventEntity> CaptureTrackedEvents()
    {
        var events = new HashSet<AdminRealtimeOutboxEventEntity>(ReferenceEqualityComparer.Instance);
        events.UnionWith(context.ChangeTracker
            .Entries<AdminRealtimeOutboxEventEntity>()
            .Select(entry => entry.Entity));
        return events;
    }

    private void DetachNewlyAddedEvents(IReadOnlySet<AdminRealtimeOutboxEventEntity> previouslyTrackedEvents)
    {
        foreach (var entry in context.ChangeTracker.Entries<AdminRealtimeOutboxEventEntity>()
                     .Where(entry => !previouslyTrackedEvents.Contains(entry.Entity)))
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }
}
