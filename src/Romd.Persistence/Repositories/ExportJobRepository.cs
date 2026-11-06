using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Export;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ExportJobRepository : IJobRepository<ExportJob>, IExportArtifactCleanupStore, IExportJobAcceptance
{
    private static readonly string PendingPhase = ExportPhase.Pending.ToString();
    private static readonly string CollectingPhase = ExportPhase.Collecting.ToString();
    private static readonly string PackagingPhase = ExportPhase.Packaging.ToString();
    private static readonly string StoringPhase = ExportPhase.Storing.ToString();

    private static readonly string[] TerminalPhases =
    [
        ExportPhase.Completed.ToString(),
        ExportPhase.CompletedWithErrors.ToString(),
        ExportPhase.Failed.ToString(),
        ExportPhase.Cancelled.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ExportJobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ExportJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<ExportJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<ExportJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<ExportJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public Task AddStagedAsync(ExportJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireAcceptanceTransaction();
        var entity = ExportJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ExportJobEntity>().Add(entity);
        return Task.CompletedTask;
    }

    public Task UpdateStagedAsync(ExportJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        var entity = ExportJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ExportJobEntity>().Update(entity);
        return Task.CompletedTask;
    }

    public async Task<bool> LockAndHasPendingStagedAsync(CancellationToken ct = default)
    {
        RequireAcceptanceTransaction();
        await JobAcceptanceLock.AcquireAsync(_context, 12903, 0, ct);
        return await _context.Jobs
            .OfType<ExportJobEntity>()
            .AnyAsync(j =>
                j.Phase == PendingPhase
                || j.Phase == CollectingPhase
                || j.Phase == PackagingPhase
                || j.Phase == StoringPhase, ct);
    }

    private void RequireAcceptanceTransaction()
    {
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Export acceptance requires a caller-owned transaction.");
    }

    public async Task<IReadOnlyList<ExpiredExportArtifact>> GetExpiredArtifactsAsync(
        DateTimeOffset completedBefore,
        CancellationToken ct = default)
    {
        return await _context.Jobs
            .OfType<ExportJobEntity>()
            .Where(j => TerminalPhases.Contains(j.Phase)
                && j.ExportPath != null
                && j.CompletedAt != null
                && j.CompletedAt < completedBefore)
            .Select(job => new ExpiredExportArtifact(job.Id, job.ExportPath!))
            .ToListAsync(ct);
    }

    public async Task<bool> ClearExpiredArtifactStagedAsync(
        Guid jobId,
        string expectedPath,
        DateTimeOffset completedBefore,
        DateTimeOffset clearedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPath);
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Export artifact cleanup requires a caller-owned transaction.");
        return await _context.Jobs.OfType<ExportJobEntity>()
            .Where(job => job.Id == jobId && job.ExportPath == expectedPath
                && TerminalPhases.Contains(job.Phase) && job.CompletedAt != null
                && job.CompletedAt < completedBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.ExportPath, (string?)null)
                .SetProperty(job => job.UpdatedAt, clearedAt), ct) == 1;
    }
}
