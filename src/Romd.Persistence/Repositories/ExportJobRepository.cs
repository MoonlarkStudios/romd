using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ExportJobRepository : IJobRepository<ExportJob>
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

    public async Task AddAsync(ExportJob job, CancellationToken ct = default)
    {
        var entity = ExportJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ExportJobEntity>().Add(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ExportJob job, CancellationToken ct = default)
    {
        var entity = ExportJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ExportJobEntity>().Update(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> HasPendingAsync(CancellationToken ct = default)
    {
        await JobAcceptanceLock.AcquireAsync(_context, 12903, 0, ct);
        return await _context.Jobs
            .OfType<ExportJobEntity>()
            .AnyAsync(j =>
                j.Phase == PendingPhase
                || j.Phase == CollectingPhase
                || j.Phase == PackagingPhase
                || j.Phase == StoringPhase, ct);
    }

    public async Task<IReadOnlyList<ExportJob>> GetCompletedOlderThanAsync(
        TimeSpan age,
        CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - age;

        var entities = await _context.Jobs
            .OfType<ExportJobEntity>()
            .Where(j => TerminalPhases.Contains(j.Phase)
                && j.ExportPath != null
                && j.CompletedAt != null
                && j.CompletedAt < cutoff)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }
}
