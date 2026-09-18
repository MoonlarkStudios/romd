using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class EnrichmentJobRepository : IEnrichmentJobRepository
{
    private static readonly string PendingPhase = EnrichmentJobPhase.Pending.ToString();
    private static readonly string EnrichingPhase = EnrichmentJobPhase.Enriching.ToString();

    private static readonly string[] TerminalPhases =
    [
        EnrichmentJobPhase.Completed.ToString(),
        EnrichmentJobPhase.Failed.ToString(),
        EnrichmentJobPhase.Cancelled.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly JobExecutionClaimOptions _claimOptions;

    public EnrichmentJobRepository(RomdDbContext context, TimeProvider timeProvider)
        : this(context, timeProvider, new JobExecutionClaimOptions())
    {
    }

    public EnrichmentJobRepository(
        RomdDbContext context,
        TimeProvider timeProvider,
        JobExecutionClaimOptions claimOptions)
    {
        _context = context;
        _timeProvider = timeProvider;
        _claimOptions = claimOptions;
    }

    public async Task<EnrichmentJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<EnrichmentJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<EnrichmentJob?> GetActiveForTitleAsync(int titleId, CancellationToken ct = default)
    {
        await JobAcceptanceLock.AcquireAsync(_context, 12901, titleId, ct);
        var entity = await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .Where(j => j.TitleId == titleId && !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity?.ToDomain();
    }

    public Task AddStagedAsync(EnrichmentJob job, CancellationToken ct = default)
    {
        var entity = EnrichmentJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<EnrichmentJobEntity>().Add(entity);
        return Task.CompletedTask;
    }

    public async Task AddAsync(EnrichmentJob job, CancellationToken ct = default)
    {
        var entity = EnrichmentJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        var entry = _context.Set<EnrichmentJobEntity>().Add(entity);
        await _context.SaveChangesAsync(ct);
        entry.State = EntityState.Detached;
    }

    public async Task SetHangfireJobIdWhilePendingAsync(
        Guid jobId,
        string hangfireJobId,
        CancellationToken ct = default)
    {
        // Narrow one-property update guarded on the Pending phase: once the single-threaded
        // enrichment worker starts the job it records its own Hangfire ID, and a stale Pending
        // snapshot must never overwrite an already-running phase. Zero updated rows is a no-op.
        await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .Where(job => job.Id == jobId && job.Phase == PendingPhase)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(job => job.HangfireJobId, hangfireJobId),
                ct);
    }

    public async Task UpdateAsync(EnrichmentJob job, CancellationToken ct = default)
    {
        var entity = EnrichmentJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<EnrichmentJobEntity>().Update(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<JobExecutionClaim<EnrichmentJob>?> TryClaimExecutionAsync(
        Guid jobId,
        string deliveryId,
        CancellationToken ct = default)
    {
        var fenceToken = Guid.NewGuid();
        int claimed = await EnrichmentExecutionClaim.TryClaimAsync<EnrichmentJobEntity>(
            _context,
            jobId,
            deliveryId,
            fenceToken,
            PendingPhase,
            EnrichingPhase,
            _timeProvider.GetUtcNow(),
            _claimOptions.LeaseDuration,
            ct);

        if (claimed != 1) return null;
        var job = await GetByIdAsync(jobId, ct);
        return job is null ? null : new JobExecutionClaim<EnrichmentJob>(job, fenceToken);
    }

    public async Task<bool> RenewExecutionLeaseAsync(
        Guid jobId,
        Guid fenceToken,
        CancellationToken ct = default) =>
        await EnrichmentExecutionClaim.RenewAsync<EnrichmentJobEntity>(
            _context,
            jobId,
            fenceToken,
            _timeProvider.GetUtcNow(),
            _claimOptions.LeaseDuration,
            ct) == 1;

    public Task<bool> HasExecutionOwnershipAsync(
        Guid jobId,
        Guid fenceToken,
        CancellationToken ct = default) =>
        EnrichmentExecutionClaim.HasOwnershipAsync<EnrichmentJobEntity>(
            _context,
            jobId,
            fenceToken,
            _timeProvider.GetUtcNow(),
            ct);

    public async Task<bool> TryUpdateClaimedAsync(
        EnrichmentJob job,
        Guid fenceToken,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        Guid? nextFence = job.IsTerminal ? null : fenceToken;
        DateTimeOffset? nextLease = job.IsTerminal ? null : now + _claimOptions.LeaseDuration;
        string errorsJson = JsonSerializer.Serialize(job.Errors.ToList());
        int updated = await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .Where(entity => entity.Id == job.Id
                && entity.ExecutionFenceToken == fenceToken
                && entity.ExecutionLeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.Phase, job.Phase)
                .SetProperty(entity => entity.HangfireJobId, job.HangfireJobId)
                .SetProperty(entity => entity.CurrentItem, job.CurrentItem)
                .SetProperty(entity => entity.ErrorsJson, errorsJson)
                .SetProperty(entity => entity.StartedAt, job.StartedAt)
                .SetProperty(entity => entity.CompletedAt, job.CompletedAt)
                .SetProperty(entity => entity.IsArchived, job.IsArchived)
                .SetProperty(entity => entity.ArchivedAt, job.ArchivedAt)
                .SetProperty(entity => entity.ExecutionFenceToken, nextFence)
                .SetProperty(entity => entity.ExecutionLeaseExpiresAtUtc, nextLease)
                .SetProperty(entity => entity.UpdatedAt, now), ct);
        return updated == 1;
    }

    /// <summary>
    ///     Checks if there's already a pending enrichment job for the given title.
    /// </summary>
    public async Task<bool> HasPendingForTitleAsync(int titleId, CancellationToken ct = default)
    {
        await JobAcceptanceLock.AcquireAsync(_context, 12901, titleId, ct);
        return await _context.Jobs
            .OfType<EnrichmentJobEntity>()
            .AnyAsync(j => j.TitleId == titleId
                && (j.Phase == PendingPhase || j.Phase == EnrichingPhase), ct);
    }
}
