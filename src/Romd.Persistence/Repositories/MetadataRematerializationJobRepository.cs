using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
namespace Romd.Persistence.Repositories;

public sealed class MetadataRematerializationJobRepository(RomdDbContext db) : IMetadataRematerializationJobRepository
{
    public async Task<MetadataRematerializationJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await db.Set<MetadataRematerializationJobEntity>().SingleOrDefaultAsync(x => x.Id == id, ct))?.ToDomain();
    public async Task<IReadOnlyList<MetadataRematerializationJob>> GetActiveAsync(CancellationToken ct = default) =>
        (await db.Set<MetadataRematerializationJobEntity>().Where(x => x.CompletedAt == null).ToListAsync(ct)).Select(x => x.ToDomain()).ToArray();
    public Task<Guid?> GetPendingIdAsync(int platformId, CancellationToken ct = default) =>
        db.Set<MetadataRematerializationJobEntity>().Where(x => x.PlatformId == platformId && x.CompletedAt == null)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    public async Task<MetadataRematerializationJob?> GetReusablePendingAsync(int platformId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Pending job reuse requires the policy transaction.");
        // The row lock serializes reuse with claim's UPDATE. PostgreSQL rechecks the predicate after a competing claim commits.
        var rows = await db.Set<MetadataRematerializationJobEntity>().FromSqlInterpolated($"""
            SELECT * FROM romd."Jobs" WHERE "JobType" = 'metadata-rematerialization'
              AND "PlatformId" = {platformId} AND "Phase" = 'Pending'
              AND "StartedAt" IS NULL AND "ExecutionFenceToken" IS NULL
            ORDER BY "CreatedAt" DESC, "Id" DESC LIMIT 1 FOR UPDATE
            """).ToListAsync(ct);
        return rows.SingleOrDefault()?.ToDomain();
    }
    public Task AddAsync(MetadataRematerializationJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Metadata job acceptance requires a policy transaction.");
        db.Add(MetadataRematerializationJobEntity.FromDomain(job));
        return Task.CompletedTask;
    }
    public async Task UpdateStagedAsync(MetadataRematerializationJob job, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        string errors = System.Text.Json.JsonSerializer.Serialize(job.Errors.ToList());
        int updated = await db.Set<MetadataRematerializationJobEntity>().Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Phase, job.Phase).SetProperty(x => x.TotalTitles, job.TotalTitles)
            .SetProperty(x => x.ProcessedCount, job.ProcessedCount).SetProperty(x => x.DeferredCount, job.DeferredCount)
            .SetProperty(x => x.CursorTitleId, job.CursorTitleId).SetProperty(x => x.HangfireJobId, job.HangfireJobId)
            .SetProperty(x => x.CurrentItem, job.CurrentItem).SetProperty(x => x.ErrorsJson, errors)
            .SetProperty(x => x.StartedAt, job.StartedAt).SetProperty(x => x.CompletedAt, job.CompletedAt)
            .SetProperty(x => x.IsArchived, job.IsArchived).SetProperty(x => x.ArchivedAt, job.ArchivedAt)
            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (updated != 1)
            throw new InvalidOperationException("Metadata checkpoint requires an existing job.");
    }
}

public sealed class MetadataRematerializationTitles(RomdDbContext db) : IMetadataRematerializationTitles
{
    public Task<int> CountAsync(int platformId, CancellationToken ct = default) =>
        db.Titles.CountAsync(x => x.PlatformId == platformId, ct);
    public async Task<IReadOnlyList<int>> ReadPageAsync(int platformId, int afterTitleId, int limit, CancellationToken ct = default) =>
        await db.Titles.Where(x => x.PlatformId == platformId && x.Id > afterTitleId).OrderBy(x => x.Id)
            .Select(x => x.Id).Take(limit).ToListAsync(ct);
}
