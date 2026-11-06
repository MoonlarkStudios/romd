using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
namespace Romd.Persistence.Repositories;

public sealed class ArtworkCompletionJobRepository(RomdDbContext db) : IJobRepository<ArtworkCompletionJob>
{
    public async Task<ArtworkCompletionJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await db.Set<ArtworkCompletionJobEntity>().SingleOrDefaultAsync(x => x.Id == id, ct))?.ToDomain();
    public async Task<IReadOnlyList<ArtworkCompletionJob>> GetActiveAsync(CancellationToken ct = default) =>
        (await db.Set<ArtworkCompletionJobEntity>().Where(x => x.Phase == "Pending" || x.Phase == "Enriching").ToListAsync(ct)).Select(x => x.ToDomain()).ToArray();
    public Task AddAsync(ArtworkCompletionJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Completion acceptance requires a caller-owned transaction.");
        db.Add(ArtworkCompletionJobEntity.FromDomain(job));
        return Task.CompletedTask;
    }
    public async Task UpdateStagedAsync(ArtworkCompletionJob job, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        // The claimed repository owns the transaction and lease fence. Only checkpoint fields may change.
        var errors = System.Text.Json.JsonSerializer.Serialize(job.Errors.ToList());
        var updated = await db.Set<ArtworkCompletionJobEntity>().Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Phase, job.Phase).SetProperty(x => x.Processed, job.Processed)
            .SetProperty(x => x.HangfireJobId, job.HangfireJobId).SetProperty(x => x.CurrentItem, job.CurrentItem)
            .SetProperty(x => x.ErrorsJson, errors).SetProperty(x => x.StartedAt, job.StartedAt)
            .SetProperty(x => x.CompletedAt, job.CompletedAt).SetProperty(x => x.IsArchived, job.IsArchived)
            .SetProperty(x => x.ArchivedAt, job.ArchivedAt).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (updated != 1)
            throw new InvalidOperationException("Completion checkpoint requires an existing job.");
    }
}
