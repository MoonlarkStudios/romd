using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class MaterializationJobRepository : IMaterializationJobRepository
{
    private static readonly string[] TerminalPhases =
    [
        MaterializationPhase.Completed.ToString(),
        MaterializationPhase.CompletedWithErrors.ToString(),
        MaterializationPhase.Failed.ToString(),
        MaterializationPhase.Cancelled.ToString(),
        MaterializationPhase.Deferred.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public MaterializationJobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<MaterializationJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<MaterializationJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<MaterializationJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<MaterializationJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<MaterializationJob?> GetActiveForLibraryAsync(
        int libraryId,
        CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<MaterializationJobEntity>()
            .Where(job => job.LibraryId == libraryId && !TerminalPhases.Contains(job.Phase))
            .SingleOrDefaultAsync(ct);

        return entity?.ToDomain();
    }

    public Task AddStagedAsync(MaterializationJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Materialization job acceptance requires a caller-owned transaction.");
        var entity = MaterializationJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<MaterializationJobEntity>().Add(entity);
        return Task.CompletedTask;
    }

    public async Task SetHangfireJobIdAsync(
        Guid jobId,
        string hangfireJobId,
        CancellationToken ct = default)
    {
        var updatedRows = await _context.Jobs
            .OfType<MaterializationJobEntity>()
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(job => job.HangfireJobId, hangfireJobId),
                ct);
        if (updatedRows != 1)
        {
            throw new InvalidOperationException(
                $"Expected to record the Hangfire ID for one materialization job, updated {updatedRows}.");
        }
    }

    public Task UpdateStagedAsync(MaterializationJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        var entity = MaterializationJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<MaterializationJobEntity>().Update(entity);
        return Task.CompletedTask;
    }
}
