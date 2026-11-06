using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Repository for ReplaceDatJob persistence.
/// </summary>
public sealed class ReplaceDatJobRepository : IReplaceDatJobRepository, IReplaceDatJobAcceptance
{
    private static readonly string[] TerminalPhases =
    [
        ReplaceDatPhase.Completed.ToString(),
        ReplaceDatPhase.CompletedWithErrors.ToString(),
        ReplaceDatPhase.Failed.ToString(),
        ReplaceDatPhase.Cancelled.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ReplaceDatJobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ReplaceDatJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<ReplaceDatJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<ReplaceDatJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<ReplaceDatJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetStaleStartedIdsAsync(
        DateTimeOffset progressBefore,
        CancellationToken ct = default)
    {
        // Same progress semantics as the upload stale-job policy: a checkpoint persists
        // the job row and refreshes UpdatedAt, so "no progress" means no row update.
        return await _context.Jobs
            .OfType<ReplaceDatJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .Where(j => j.StartedAt != null)
            .Where(j => j.UpdatedAt < progressBefore)
            .OrderBy(j => j.CreatedAt)
            .Select(job => job.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> TryFailStaleStartedStagedAsync(
        Guid jobId,
        DateTimeOffset progressBefore,
        string reason,
        CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Stale job failure requires a caller-owned transaction.");
        var rows = await _context.Set<ReplaceDatJobEntity>().FromSqlInterpolated($"""
            SELECT * FROM romd."Jobs"
            WHERE "Id" = {jobId} AND "JobType" = 'replace_dat'
            FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        var current = rows.SingleOrDefault();
        if (current is null || current.StartedAt is null || TerminalPhases.Contains(current.Phase)
            || current.UpdatedAt >= progressBefore)
            return false;

        // Materialization may wait on a competing writer. Failure happens after that awaited read.
        var job = current.ToDomain();
        var failedAt = _timeProvider.GetUtcNow();
        job.Fail(reason, failedAt);
        var errors = JsonSerializer.Serialize(job.Errors);
        return await _context.Jobs.OfType<ReplaceDatJobEntity>()
            .Where(row => row.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Phase, job.Phase)
                .SetProperty(row => row.CompletedAt, job.CompletedAt)
                .SetProperty(row => row.ErrorsJson, errors)
                .SetProperty(row => row.UpdatedAt, failedAt)
                .SetProperty(row => row.ExecutionFenceToken, (Guid?)null)
                .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, (DateTimeOffset?)null), ct) == 1;
    }

    public Task AddStagedAsync(ReplaceDatJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("DAT replacement acceptance requires a caller-owned transaction.");
        _context.Set<ReplaceDatJobEntity>().Add(ReplaceDatJobEntity.FromDomain(job, _timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }

    public Task UpdateStagedAsync(ReplaceDatJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        var entity = ReplaceDatJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ReplaceDatJobEntity>().Update(entity);
        return Task.CompletedTask;
    }
}
