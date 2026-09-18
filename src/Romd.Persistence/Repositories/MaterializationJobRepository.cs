using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
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
        Stage(job);
        return Task.CompletedTask;
    }

    public async Task AddAsync(MaterializationJob job, CancellationToken ct = default)
    {
        var entry = Stage(job);
        await _context.SaveChangesAsync(ct);
        entry.State = EntityState.Detached;
    }

    public async Task<bool> TryAddIfNoActiveForLibraryAsync(MaterializationJob job, CancellationToken ct = default)
    {
        try
        {
            await AddAsync(job, ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _context.ChangeTracker.Clear();
            return false;
        }
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

    public async Task UpdateAsync(MaterializationJob job, CancellationToken ct = default)
    {
        var entity = MaterializationJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<MaterializationJobEntity>().Update(entity);
        await _context.SaveChangesAsync(ct);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private EntityEntry<MaterializationJobEntity> Stage(MaterializationJob job)
    {
        var entity = MaterializationJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        return _context.Set<MaterializationJobEntity>().Add(entity);
    }
}
