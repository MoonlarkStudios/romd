using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Dispatch checkpoints cannot replace SQL-owned demand, source custody, progress or terminal journals.</summary>
public sealed class MediaReconciliationJobRepository(RomdDbContext context, TimeProvider clock)
    : IJobRepository<MediaReconciliationJob>, IEnrichmentDispatchRepository
{
    private IQueryable<MediaReconciliationJobEntity> Readable() => context.Set<MediaReconciliationJobEntity>()
        .AsNoTracking().Include(row => row.Definition).ThenInclude(definition => definition.Outcome);
    public async Task<MediaReconciliationJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await Readable().SingleOrDefaultAsync(row => row.Id == id, ct))?.ToDomain();
    public async Task<IReadOnlyList<MediaReconciliationJob>> GetActiveAsync(CancellationToken ct = default) =>
        (await Readable().Where(row => row.CompletedAt == null).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToListAsync(ct))
            .Select(row => row.ToDomain()).ToArray();
    public Task AddAsync(MediaReconciliationJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        context.Add(MediaReconciliationJobEntity.FromDomain(job, clock.GetUtcNow()));
        return Task.CompletedTask;
    }
    public async Task UpdateStagedAsync(MediaReconciliationJob job, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        // The execution store owns the phase's durable outcome. A stale mapper may change archive metadata only after settlement.
        await context.Set<MediaReconciliationJobEntity>().Where(row => row.Id == job.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.IsArchived, job.IsArchived).SetProperty(row => row.ArchivedAt, job.ArchivedAt), ct);
        string errors = JsonSerializer.Serialize(job.Errors);
        await context.Set<MediaReconciliationJobEntity>().Where(row => row.Id == job.Id &&
                !context.Set<MediaReconciliationOutcomeEntity>().Any(outcome => outcome.JobId == row.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Phase, job.Phase).SetProperty(row => row.HangfireJobId, job.HangfireJobId)
                .SetProperty(row => row.CurrentItem, job.CurrentItem).SetProperty(row => row.ErrorsJson, errors)
                .SetProperty(row => row.StartedAt, job.StartedAt).SetProperty(row => row.UpdatedAt, clock.GetUtcNow())
                .SetProperty(row => row.NextAttemptAtUtc, job.NextAttemptAt).SetProperty(row => row.DeferralCount, job.DeferralCount)
                .SetProperty(row => row.LastDeferralError, job.LastDeferralError).SetProperty(row => row.LastDeferralAt, job.LastDeferralAt)
                .SetProperty(row => row.FirstDeferralAt, job.FirstDeferralAt), ct);
    }
    public async Task SetHangfireJobIdWhilePendingAsync(Guid jobId, string hangfireJobId, CancellationToken ct = default) =>
        _ = await context.Set<MediaReconciliationJobEntity>().Where(row => row.Id == jobId && row.Phase == "Pending")
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.HangfireJobId, hangfireJobId), ct);
}
