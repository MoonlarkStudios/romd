using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Realtime;
using Romd.Persistence.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Realtime;

public sealed class AdminRealtimeOutbox(
    RomdDbContext context,
    TimeProvider timeProvider) : IAdminEventOutbox, IAdminRealtimeOutbox
{
    private const int MaxLastErrorLength = 2000;

    public Task EnqueueAsync(string eventType, CancellationToken ct = default) =>
        EnqueueAsync(eventType, "{}", ct);

    /// <summary>
    ///     Enlists the event intent in the scoped context without saving. The
    ///     caller owns the commit; the row becomes durable with the caller's
    ///     next SaveChanges/transaction commit.
    /// </summary>
    public Task EnqueueAsync<TPayload>(string eventType, TPayload payload, CancellationToken ct = default)
    {
        string payloadJson = payload is string json
            ? json
            : AdminRealtimePayloadSerializer.Serialize(payload);

        var now = timeProvider.GetUtcNow();
        context.AdminRealtimeOutboxEvents.Add(new AdminRealtimeOutboxEventEntity
        {
            EventType = eventType,
            PayloadJson = payloadJson,
            SchemaVersion = AdminRealtimeSchemaVersions.CurrentFor(eventType),
            CreatedAtUtc = now,
            AvailableAtUtc = now
        });

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AdminRealtimeOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        if (batchSize <= 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        string claimId = Guid.NewGuid().ToString("N");
        var leasedUntil = now + leaseDuration;

        var ids = await context.AdminRealtimeOutboxEvents
            .Where(row => row.ProcessedAtUtc == null && row.AvailableAtUtc <= now)
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .Select(row => row.Id)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            return [];
        }

        await context.AdminRealtimeOutboxEvents
            .Where(row => ids.Contains(row.Id)
                          && row.ProcessedAtUtc == null
                          && row.AvailableAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ClaimId, claimId)
                .SetProperty(row => row.ClaimedAtUtc, now)
                .SetProperty(row => row.AvailableAtUtc, leasedUntil)
                .SetProperty(row => row.Attempts, row => row.Attempts + 1),
                ct);

        return await context.AdminRealtimeOutboxEvents
            .AsNoTracking()
            .Where(row => row.ClaimId == claimId)
            .OrderBy(row => row.Id)
            .Select(row => new AdminRealtimeOutboxMessage(
                row.Id,
                row.EventType,
                row.PayloadJson,
                row.SchemaVersion,
                row.Attempts))
            .ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(int id, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();

        await context.AdminRealtimeOutboxEvents
            .Where(row => row.Id == id && row.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ProcessedAtUtc, now)
                .SetProperty(row => row.ClaimId, (string?)null)
                .SetProperty(row => row.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.LastError, (string?)null),
                ct);
    }

    public async Task MarkFailedAsync(int id, string error, TimeSpan retryDelay, CancellationToken ct = default)
    {
        var nextAttemptAt = timeProvider.GetUtcNow() + retryDelay;
        string lastError = error.Length <= MaxLastErrorLength
            ? error
            : error[..MaxLastErrorLength];

        await context.AdminRealtimeOutboxEvents
            .Where(row => row.Id == id && row.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AvailableAtUtc, nextAttemptAt)
                .SetProperty(row => row.LastError, lastError)
                .SetProperty(row => row.ClaimId, (string?)null)
                .SetProperty(row => row.ClaimedAtUtc, (DateTimeOffset?)null),
                ct);
    }

    public async Task<int> DeleteProcessedOlderThanAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow() - olderThan;

        return await context.AdminRealtimeOutboxEvents
            .Where(row => row.ProcessedAtUtc != null && row.ProcessedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
