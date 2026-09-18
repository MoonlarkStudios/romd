using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ArtworkImportJobRepository : IJobRepository<ArtworkImportJob>
{
    private static readonly string[] TerminalPhases =
    [
        nameof(ArtworkImportPhase.Completed),
        nameof(ArtworkImportPhase.Failed),
        nameof(ArtworkImportPhase.Cancelled)
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ArtworkImportJobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ArtworkImportJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs.OfType<ArtworkImportJobEntity>().AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, ct);
        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<ArtworkImportJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs.OfType<ArtworkImportJobEntity>().AsNoTracking()
            .Where(job => !TerminalPhases.Contains(job.Phase))
            .OrderBy(job => job.CreatedAt).ThenBy(job => job.Id).ToListAsync(ct);
        return entities.Select(job => job.ToDomain()).ToArray();
    }

    /// <summary>Stages only: acceptance owns the job, selection and dispatch commit.</summary>
    public Task AddAsync(ArtworkImportJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Artwork import acceptance requires a caller-owned transaction.");
        _context.Set<ArtworkImportJobEntity>().Add(ArtworkImportJobEntity.FromDomain(job, _timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A narrow atomic checkpoint. ClaimedJobRepository owns the fence check and
    /// transaction; identity and execution credentials are intentionally untouched.
    /// </summary>
    public async Task UpdateAsync(ArtworkImportJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var clearPending = job.PhaseEnum is ArtworkImportPhase.Failed or ArtworkImportPhase.Cancelled;
        if (clearPending && _context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Terminal artwork updates require a caller-owned transaction.");
        var errorsJson = JsonSerializer.Serialize(job.Errors.ToList());
        var now = _timeProvider.GetUtcNow();
        var updated = await _context.Jobs.OfType<ArtworkImportJobEntity>()
            .Where(entity => entity.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.Phase, job.Phase)
                .SetProperty(entity => entity.HangfireJobId, job.HangfireJobId)
                .SetProperty(entity => entity.CurrentItem, job.CurrentItem)
                .SetProperty(entity => entity.ErrorsJson, errorsJson)
                .SetProperty(entity => entity.StartedAt, job.StartedAt)
                .SetProperty(entity => entity.CompletedAt, job.CompletedAt)
                .SetProperty(entity => entity.IsArchived, job.IsArchived)
                .SetProperty(entity => entity.ArchivedAt, job.ArchivedAt)
                // Publication may have committed even when its acknowledgement failed.
                // A stale retry checkpoint must never erase that durable outcome.
                .SetProperty(entity => entity.RetainedAssetId, entity => entity.RetainedAssetId ?? job.RetainedAssetId)
                .SetProperty(entity => entity.WasSuperseded, entity => entity.WasSuperseded || job.WasSuperseded)
                .SetProperty(entity => entity.UpdatedAt, now), ct);
        if (updated != 1)
            throw new InvalidOperationException($"Expected one artwork import job checkpoint, updated {updated}.");
        if (clearPending)
            await _context.ArtworkSelections
                .Where(selection => selection.TitleId == job.TitleId && selection.Role == job.Role &&
                    selection.Revision == job.SelectionRevision && selection.PendingRequestId == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(selection => selection.PendingRequestId, (Guid?)null), ct);
    }
}
