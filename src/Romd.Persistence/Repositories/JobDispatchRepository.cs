using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class JobDispatchRepository(RomdDbContext context, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<Guid>> GetAvailableAsync(int limit, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        return await context.JobDispatches
            .Where(dispatch => (dispatch.DeliveredAtUtc == null || context.Jobs.Any(job =>
                    job.Id == dispatch.JobId && job.CompletedAt == null
                    && job.ExecutionFenceToken != null && job.ExecutionLeaseExpiresAtUtc <= now))
                && dispatch.AvailableAtUtc <= now
                && (dispatch.LeaseExpiresAtUtc == null || dispatch.LeaseExpiresAtUtc <= now))
            .OrderBy(dispatch => dispatch.AvailableAtUtc).ThenBy(dispatch => dispatch.JobId)
            .Take(limit).Select(dispatch => dispatch.JobId).ToListAsync(ct);
    }

    public async Task<JobDispatchEntity?> TryClaimAsync(Guid jobId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid();
        int claimed = await context.JobDispatches
            .Where(dispatch => dispatch.JobId == jobId
                && (dispatch.DeliveredAtUtc == null || context.Jobs.Any(job =>
                    job.Id == dispatch.JobId && job.CompletedAt == null
                    && job.ExecutionFenceToken != null && job.ExecutionLeaseExpiresAtUtc <= now))
                && dispatch.AvailableAtUtc <= now
                && (dispatch.LeaseExpiresAtUtc == null || dispatch.LeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(dispatch => dispatch.ClaimToken, token)
                .SetProperty(dispatch => dispatch.DeliveredAtUtc, (DateTimeOffset?)null)
                .SetProperty(dispatch => dispatch.AvailableAtUtc, now.AddMinutes(1))
                .SetProperty(dispatch => dispatch.LeaseExpiresAtUtc, now.AddMinutes(1))
                .SetProperty(dispatch => dispatch.AttemptCount, dispatch => dispatch.AttemptCount + 1), ct);
        return claimed == 1
            ? await context.JobDispatches.SingleAsync(dispatch => dispatch.JobId == jobId, ct)
            : null;
    }

    public async Task AcknowledgeAsync(JobDispatchEntity dispatch, string deliveryId, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        int acknowledged = await context.JobDispatches
            .Where(row => row.JobId == dispatch.JobId && row.ClaimToken == dispatch.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.DeliveredAtUtc, timeProvider.GetUtcNow())
                .SetProperty(row => row.HangfireJobId, deliveryId)
                .SetProperty(row => row.ClaimToken, (Guid?)null)
                .SetProperty(row => row.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.LastError, (string?)null), ct);
        if (acknowledged == 1)
        {
            // A delivery can begin before acknowledgement. Never replace an executor's identity.
            await context.Jobs.Where(job => job.Id == dispatch.JobId && job.Phase == "Pending"
                    && job.HangfireJobId == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.HangfireJobId, deliveryId), ct);
        }
        await transaction.CommitAsync(ct);
    }

    public Task RetryAsync(JobDispatchEntity dispatch, string error, CancellationToken ct) =>
        context.JobDispatches.Where(row => row.JobId == dispatch.JobId && row.ClaimToken == dispatch.ClaimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ClaimToken, (Guid?)null)
                .SetProperty(row => row.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.AvailableAtUtc, timeProvider.GetUtcNow().AddSeconds(30))
                .SetProperty(row => row.LastError, error.Length > 1000 ? error.Substring(0, 1000) : error), ct);
}
