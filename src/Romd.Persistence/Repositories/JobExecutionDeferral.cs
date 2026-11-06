using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Repositories;

/// <summary>Releases an unfinished execution and rearms its outbox in the same fenced transaction.</summary>
internal static class JobExecutionDeferral
{
    public static async Task<bool> DeferAsync(
        RomdDbContext context, Job job, Guid fenceToken, DateTimeOffset until, DateTimeOffset now,
        Func<CancellationToken, Task<bool>> checkpoint, CancellationToken ct, ILogger? logger = null)
    {
        if (job.IsTerminal)
            throw new InvalidOperationException("A terminal job cannot be deferred.");
        var transaction = await context.Database.BeginTransactionAsync(ct);
        bool commitAcknowledged = false;
        Exception? failure = null;
        try
        {
            // Lock the execution row before checkpointing or touching its dispatch record.
            int owned = await context.Jobs.Where(row => row.Id == job.Id
                    && row.ExecutionFenceToken == fenceToken && row.ExecutionLeaseExpiresAtUtc > now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, now), ct);
            if (owned != 1 || !await checkpoint(ct))
                return false;

            await context.Jobs.Where(row => row.Id == job.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.NextAttemptAtUtc, until)
                .SetProperty(row => row.ExecutionFenceToken, (Guid?)null)
                .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.HangfireJobId, (string?)null), ct);
            int rearmed = await context.JobDispatches.Where(row => row.JobId == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.AvailableAtUtc, until)
                    .SetProperty(row => row.DeliveredAtUtc, (DateTimeOffset?)null)
                    .SetProperty(row => row.ClaimToken, (Guid?)null)
                    .SetProperty(row => row.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                    .SetProperty(row => row.HangfireJobId, (string?)null)
                    .SetProperty(row => row.LastError, job.LastDeferralError), ct);
            if (rearmed != 1)
                throw new InvalidOperationException("Deferred job has no durable dispatch record.");
            await transaction.CommitAsync(ct);
            commitAcknowledged = true;
            context.ChangeTracker.Clear();
            // Realtime publication of the in-memory job must show when the durable schedule resumes it.
            job.NextAttemptAt = until;
            return true;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { await TransactionCleanup.DisposeAsync(transaction, commitAcknowledged, failure, logger); }
    }
}
