using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

internal static class EnrichmentExecutionClaim
{
    public static Task<int> TryClaimAsync<TEntity>(
        RomdDbContext context,
        Guid jobId,
        string deliveryId,
        Guid fenceToken,
        string pendingPhase,
        string activePhase,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct) where TEntity : JobEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var leaseExpiresAt = now + leaseDuration;

        // ExecuteUpdate is the durability boundary: the active phase, delivery identity,
        // and lease heartbeat commit in one statement before the repository hydrates the
        // winner. If that subsequent read or the worker process fails, only the same delivery
        // can reclaim after the heartbeat expires.
        return context.Jobs
            .OfType<TEntity>()
            .Where(job => job.Id == jobId
                && ((job.Phase == pendingPhase
                        && (job.HangfireJobId == null || job.HangfireJobId == deliveryId))
                    || (job.Phase == activePhase
                        && job.HangfireJobId == deliveryId
                        && job.ExecutionLeaseExpiresAtUtc <= now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Phase, activePhase)
                    .SetProperty(job => job.HangfireJobId, deliveryId)
                    .SetProperty(job => job.StartedAt, job => job.StartedAt ?? now)
                    .SetProperty(job => job.ExecutionFenceToken, fenceToken)
                    .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, leaseExpiresAt)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);
    }

    public static Task<int> RenewAsync<TEntity>(
        RomdDbContext context,
        Guid jobId,
        Guid fenceToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct) where TEntity : JobEntity =>
        context.Jobs
            .OfType<TEntity>()
            .Where(job => job.Id == jobId
                && job.ExecutionFenceToken == fenceToken
                && job.ExecutionLeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, now + leaseDuration)
                    .SetProperty(job => job.UpdatedAt, now),
                ct);

    public static Task<bool> HasOwnershipAsync<TEntity>(
        RomdDbContext context,
        Guid jobId,
        Guid fenceToken,
        DateTimeOffset now,
        CancellationToken ct) where TEntity : JobEntity =>
        context.Jobs
            .OfType<TEntity>()
            .AnyAsync(
                job => job.Id == jobId
                    && job.ExecutionFenceToken == fenceToken
                    && job.ExecutionLeaseExpiresAtUtc > now,
                ct);
}
