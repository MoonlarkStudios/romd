using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class UploadJobRepository : IUploadJobRepository, IJobRepository<UploadJob>, IUploadJobAcceptance
{
    // Store terminal phase names for queries
    private static readonly string[] TerminalPhases =
    [
        UploadPhase.Completed.ToString(),
        UploadPhase.CompletedWithErrors.ToString(),
        UploadPhase.Failed.ToString(),
        UploadPhase.Cancelled.ToString()
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public UploadJobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<UploadJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .OfType<UploadJobEntity>()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<UploadJob>> GetRecentAsync(
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var query = _context.Jobs.OfType<UploadJobEntity>().AsQueryable();

        if (!includeArchived)
        {
            query = query.Where(j => !j.IsArchived);
        }

        var entities = await query
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<UploadJob>> GetActiveAsync(CancellationToken ct = default)
    {
        var entities = await _context.Jobs
            .OfType<UploadJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public Task AddStagedAsync(UploadJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Upload job acceptance requires a caller-owned transaction.");
        _context.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(job, _timeProvider.GetUtcNow()));
        return Task.CompletedTask;
    }

    public Task UpdateStagedAsync(UploadJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        var entity = UploadJobEntity.FromDomain(job, _timeProvider.GetUtcNow());
        _context.Set<UploadJobEntity>().Update(entity);
        return Task.CompletedTask;
    }

    public async Task<int> PurgeArchivedAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - olderThan;

        return await _context.Jobs
            .OfType<UploadJobEntity>()
            .Where(j => j.IsArchived && j.ArchivedAt != null && j.ArchivedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> ArchiveCompletedAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();

        return await _context.Jobs
            .OfType<UploadJobEntity>()
            .Where(j => !j.IsArchived && TerminalPhases.Contains(j.Phase))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.IsArchived, true)
                    .SetProperty(j => j.ArchivedAt, now),
                ct);
    }

    public async Task<int> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - staleThreshold;
        string failedPhase = UploadPhase.Failed.ToString();
        const string staleErrorJson = """[{"Item":"job","Message":"Job timed out (no progress updates received)","OccurredAt":"0001-01-01T00:00:00+00:00"}]""";

        // Find and fail jobs that:
        // - Are not in a terminal state
        // - Have started (StartedAt is not null)
        // - Haven't been updated within the threshold
        return await _context.Jobs
            .OfType<UploadJobEntity>()
            .Where(j => !TerminalPhases.Contains(j.Phase))
            .Where(j => j.StartedAt != null)
            .Where(j => j.UpdatedAt < cutoff)
            // Accepted durable work is recovered by execution leases and the dispatch
            // outbox. Age alone must not destroy its workspace or pending retry.
            .Where(j => j.ExecutionFenceToken == null && j.NextAttemptAtUtc == null)
            .Where(j => !_context.JobDispatches.Any(dispatch => dispatch.JobId == j.Id))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Phase, failedPhase)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.UpdatedAt, now)
                    .SetProperty(j => j.ErrorsJson, staleErrorJson)
                    .SetProperty(j => j.CurrentItem, (string?)null),
                ct);
    }
}
