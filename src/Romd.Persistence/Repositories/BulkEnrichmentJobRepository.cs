using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class BulkEnrichmentJobRepository : IBulkEnrichmentJobRepository
{
    private static readonly string PendingPhase = BulkEnrichmentPhase.Pending.ToString();
    private static readonly string EnrichingPhase = BulkEnrichmentPhase.Enriching.ToString();

    private static readonly string[] TerminalPhases =
    [
        BulkEnrichmentPhase.Completed.ToString(),
        BulkEnrichmentPhase.CompletedWithErrors.ToString(),
        BulkEnrichmentPhase.Failed.ToString(),
        BulkEnrichmentPhase.Cancelled.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly JobExecutionClaimOptions _claimOptions;

    public BulkEnrichmentJobRepository(RomdDbContext context, TimeProvider timeProvider)
        : this(context, timeProvider, new JobExecutionClaimOptions())
    {
    }

    public BulkEnrichmentJobRepository(
        RomdDbContext context,
        TimeProvider timeProvider,
        JobExecutionClaimOptions claimOptions)
    {
        _context = context;
        _timeProvider = timeProvider;
        _claimOptions = claimOptions;
    }

    public async Task<BulkEnrichmentJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<BulkEnrichmentJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task AddAsync(BulkEnrichmentJob job, CancellationToken ct = default)
    {
        var entity = BulkEnrichmentJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<BulkEnrichmentJobEntity>().Add(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(BulkEnrichmentJob job, CancellationToken ct = default)
    {
        var entity = BulkEnrichmentJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<BulkEnrichmentJobEntity>().Update(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SetHangfireJobIdWhilePendingAsync(
        Guid jobId,
        string hangfireJobId,
        CancellationToken ct = default)
    {
        // Hangfire can start the single-worker enrichment job before the scheduler records
        // the returned ID. Restrict this to the Pending row so late dispatch bookkeeping can
        // never regress executor-owned phase/progress state or replace the runner's ID.
        await _context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
            .Where(job => job.Id == jobId && job.Phase == PendingPhase)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(job => job.HangfireJobId, hangfireJobId),
                ct);
    }

    public async Task<JobExecutionClaim<BulkEnrichmentJob>?> TryClaimExecutionAsync(
        Guid jobId,
        string deliveryId,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var fenceToken = Guid.NewGuid();
        int claimed = await EnrichmentExecutionClaim.TryClaimAsync<BulkEnrichmentJobEntity>(
            _context,
            jobId,
            deliveryId,
            fenceToken,
            PendingPhase,
            EnrichingPhase,
            now,
            _claimOptions.LeaseDuration,
            ct);

        if (claimed != 1) return null;
        var job = await GetByIdAsync(jobId, ct);
        return job is null ? null : new JobExecutionClaim<BulkEnrichmentJob>(job, fenceToken);
    }

    public async Task<bool> RenewExecutionLeaseAsync(
        Guid jobId,
        Guid fenceToken,
        CancellationToken ct = default) =>
        await EnrichmentExecutionClaim.RenewAsync<BulkEnrichmentJobEntity>(
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
        EnrichmentExecutionClaim.HasOwnershipAsync<BulkEnrichmentJobEntity>(
            _context,
            jobId,
            fenceToken,
            _timeProvider.GetUtcNow(),
            ct);

    public async Task<bool> TryUpdateClaimedAsync(
        BulkEnrichmentJob job,
        Guid fenceToken,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        Guid? nextFence = job.IsTerminal ? null : fenceToken;
        DateTimeOffset? nextLease = job.IsTerminal ? null : now + _claimOptions.LeaseDuration;
        string errorsJson = JsonSerializer.Serialize(job.Errors.ToList());
        int updated = await _context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
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
                .SetProperty(entity => entity.TotalTitles, job.TotalTitles)
                .SetProperty(entity => entity.ProcessedCount, job.ProcessedCount)
                .SetProperty(entity => entity.EnrichedCount, job.EnrichedCount)
                .SetProperty(entity => entity.NotFoundCount, job.NotFoundCount)
                .SetProperty(entity => entity.FailedCount, job.FailedCount)
                .SetProperty(entity => entity.SkippedCount, job.SkippedCount)
                .SetProperty(entity => entity.ExecutionFenceToken, nextFence)
                .SetProperty(entity => entity.ExecutionLeaseExpiresAtUtc, nextLease)
                .SetProperty(entity => entity.UpdatedAt, now), ct);
        return updated == 1;
    }

    /// <summary>
    ///     Checks if there's already a pending or active bulk enrichment job for the given platform.
    /// </summary>
    public async Task<bool> HasPendingForPlatformAsync(int platformId, CancellationToken ct = default)
    {
        await JobAcceptanceLock.AcquireAsync(_context, 12902, platformId, ct);
        return await _context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
            .AnyAsync(j => j.PlatformId == platformId
                && (j.Phase == PendingPhase || j.Phase == EnrichingPhase), ct);
    }
}
