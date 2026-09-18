using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Repository for ReplaceDatJob persistence.
/// </summary>
public sealed class ReplaceDatJobRepository : IReplaceDatJobRepository
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

    public async Task<IReadOnlyList<ReplaceDatJob>> GetStaleStartedAsync(
        TimeSpan staleThreshold,
        CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - staleThreshold;

        // Same progress semantics as the upload stale-job policy: a checkpoint persists
        // the job row and refreshes UpdatedAt, so "no progress" means no row update.
        var entities = await _context.Jobs
            .OfType<ReplaceDatJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .Where(j => j.StartedAt != null)
            .Where(j => j.UpdatedAt < cutoff)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task AddAsync(ReplaceDatJob job, CancellationToken ct = default)
    {
        var entity = ReplaceDatJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ReplaceDatJobEntity>().Add(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ReplaceDatJob job, CancellationToken ct = default)
    {
        var entity = ReplaceDatJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<ReplaceDatJobEntity>().Update(entity);
        await _context.SaveChangesAsync(ct);
    }
}
