using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Repositories;

/// <summary>Shared execution claim and checkpoint boundary for all durable job families.</summary>
public sealed class ClaimedJobRepository<TJob, TRepository>(
    TRepository repository,
    RomdDbContext context,
    TimeProvider timeProvider,
    JobExecutionClaimOptions options,
    ILogger<ClaimedJobRepository<TJob, TRepository>>? logger = null) : IJobExecutionClaimRepository<TJob>
    where TJob : Job
    where TRepository : IJobRepository<TJob>
{
    private static readonly string[] TerminalPhases = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred", "Superseded"];

    public Task<TJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => repository.GetByIdAsync(id, ct);
    public Task<IReadOnlyList<TJob>> GetActiveAsync(CancellationToken ct = default) => repository.GetActiveAsync(ct);
    public Task UpdateStagedAsync(TJob job, CancellationToken ct = default) => repository.UpdateStagedAsync(job, ct);

    public async Task<JobExecutionClaim<TJob>?> TryClaimExecutionAsync(Guid jobId, string deliveryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var transaction = await context.Database.BeginTransactionAsync(ct);
        bool commitAcknowledged = false;
        Exception? failure = null;
        try
        {
            var now = timeProvider.GetUtcNow();
            var token = Guid.NewGuid();
            int claimed = await context.Jobs
                .Where(job => job.Id == jobId && !TerminalPhases.Contains(job.Phase)
                    && (job.NextAttemptAtUtc == null || job.NextAttemptAtUtc <= now)
                    && (job.ExecutionFenceToken == null || job.ExecutionLeaseExpiresAtUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.NextAttemptAtUtc, (DateTimeOffset?)null)
                    .SetProperty(job => job.ExecutionFenceToken, token)
                    .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, now + options.LeaseDuration)
                    .SetProperty(job => job.HangfireJobId, deliveryId), ct);
            if (claimed != 1)
                return null;
            var job = await repository.GetByIdAsync(jobId, ct);
            if (job is null)
                return null;
            if (job.Phase == "Pending")
                // The lease snapshot precedes the awaited claim and read; execution starts at this transition.
                job.Start(deliveryId, startedAt: timeProvider.GetUtcNow());
            else
                job.SetHangfireJobId(deliveryId);
            await SaveAsync(job, token, now, ct);
            await transaction.CommitAsync(ct);
            commitAcknowledged = true;
            return new JobExecutionClaim<TJob>(job, token);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { await TransactionCleanup.DisposeAsync(transaction, commitAcknowledged, failure, logger); }
    }

    public async Task<bool> TryUpdateClaimedAsync(TJob job, Guid fenceToken, CancellationToken ct = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(ct);
        bool commitAcknowledged = false;
        Exception? failure = null;
        try
        {
            var now = timeProvider.GetUtcNow();
            int owned = await context.Jobs.Where(row => row.Id == job.Id
                    && row.ExecutionFenceToken == fenceToken && row.ExecutionLeaseExpiresAtUtc > now
                    && !TerminalPhases.Contains(row.Phase))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, now), ct);
            if (owned != 1)
                return false;
            await SaveAsync(job, fenceToken, now, ct);
            await transaction.CommitAsync(ct);
            commitAcknowledged = true;
            return true;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { await TransactionCleanup.DisposeAsync(transaction, commitAcknowledged, failure, logger); }
    }

    public Task<bool> TryDeferClaimedAsync(TJob job, Guid fenceToken, DateTimeOffset until, CancellationToken ct = default) =>
        JobExecutionDeferral.DeferAsync(context, job, fenceToken, until, timeProvider.GetUtcNow(),
            async token => { await SaveAsync(job, fenceToken, timeProvider.GetUtcNow(), token); return true; }, ct, logger);

    private async Task SaveAsync(TJob job, Guid token, DateTimeOffset now, CancellationToken ct)
    {
        // Existing domain mappers intentionally do not carry execution credentials. The row lock
        // holds through both writes so mapper defaults can never publish an unfenced active job.
        try
        {
            await repository.UpdateStagedAsync(job, ct);
            // Flush the phase while the execution row remains locked, then restore the mapper-omitted
            // credentials before commit. No observer can see an active, unfenced checkpoint.
            await context.SaveChangesAsync(ct);
            Guid? nextToken = job.IsTerminal ? null : token;
            DateTimeOffset? nextLease = job.IsTerminal ? null : now + options.LeaseDuration;
            await context.Jobs.Where(row => row.Id == job.Id && (job.IsTerminal || row.CompletedAt == null)).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ExecutionFenceToken, nextToken)
                .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, nextLease), ct);
        }
        finally
        {
            // This protocol owns a fresh checkpoint scope; failed flush/restoration must not retain
            // mapped state for a later attempt after the owning transaction rolls back.
            context.ChangeTracker.Clear();
        }
    }

    public async Task<bool> RenewExecutionLeaseAsync(Guid jobId, Guid fenceToken, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        return await context.Jobs.Where(job => job.Id == jobId && job.ExecutionFenceToken == fenceToken
                && job.ExecutionLeaseExpiresAtUtc > now && !TerminalPhases.Contains(job.Phase))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, now + options.LeaseDuration), ct) == 1;
    }

    public Task<bool> HasExecutionOwnershipAsync(Guid jobId, Guid fenceToken, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        return context.Jobs.AnyAsync(job => job.Id == jobId && job.ExecutionFenceToken == fenceToken
            && job.ExecutionLeaseExpiresAtUtc > now && !TerminalPhases.Contains(job.Phase), ct);
    }
}
