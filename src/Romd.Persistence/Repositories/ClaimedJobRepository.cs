using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Repositories;

/// <summary>Shared execution claim and checkpoint boundary for all durable job families.</summary>
public sealed class ClaimedJobRepository<TJob, TRepository>(
    TRepository repository,
    RomdDbContext context,
    TimeProvider timeProvider,
    JobExecutionClaimOptions options) : IJobExecutionClaimRepository<TJob>
    where TJob : Job
    where TRepository : IJobRepository<TJob>
{
    private static readonly string[] TerminalPhases = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];

    public Task<TJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => repository.GetByIdAsync(id, ct);
    public Task<IReadOnlyList<TJob>> GetActiveAsync(CancellationToken ct = default) => repository.GetActiveAsync(ct);
    public Task AddAsync(TJob job, CancellationToken ct = default) => repository.AddAsync(job, ct);
    public Task UpdateAsync(TJob job, CancellationToken ct = default) => repository.UpdateAsync(job, ct);

    public async Task<JobExecutionClaim<TJob>?> TryClaimExecutionAsync(Guid jobId, string deliveryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow();
        var token = Guid.NewGuid();
        int claimed = await context.Jobs
            .Where(job => job.Id == jobId && !TerminalPhases.Contains(job.Phase)
                && (job.ExecutionFenceToken == null || job.ExecutionLeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.ExecutionFenceToken, token)
                .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, now + options.LeaseDuration)
                .SetProperty(job => job.HangfireJobId, deliveryId), ct);
        if (claimed != 1) return null;
        var job = await repository.GetByIdAsync(jobId, ct);
        if (job is null) return null;
        if (job.Phase == "Pending") job.Start(deliveryId);
        else job.SetHangfireJobId(deliveryId);
        await SaveAsync(job, token, now, ct);
        await transaction.CommitAsync(ct);
        return new JobExecutionClaim<TJob>(job, token);
    }

    public async Task<bool> TryUpdateClaimedAsync(TJob job, Guid fenceToken, CancellationToken ct = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow();
        int owned = await context.Jobs.Where(row => row.Id == job.Id
                && row.ExecutionFenceToken == fenceToken && row.ExecutionLeaseExpiresAtUtc > now
                && !TerminalPhases.Contains(row.Phase))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, now), ct);
        if (owned != 1) return false;
        await SaveAsync(job, fenceToken, now, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task SaveAsync(TJob job, Guid token, DateTimeOffset now, CancellationToken ct)
    {
        // Existing domain mappers intentionally do not carry execution credentials. The row lock
        // holds through both writes so mapper defaults can never publish an unfenced active job.
        await repository.UpdateAsync(job, ct);
        Guid? nextToken = job.IsTerminal ? null : token;
        DateTimeOffset? nextLease = job.IsTerminal ? null : now + options.LeaseDuration;
        await context.Jobs.Where(row => row.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ExecutionFenceToken, nextToken)
            .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, nextLease), ct);
        context.ChangeTracker.Clear();
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
