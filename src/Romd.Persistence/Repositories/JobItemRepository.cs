using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Stores per-file upload provenance and resolves the snapshot ids (titles, platform) to
///     display names at read time.
/// </summary>
public sealed class JobItemRepository : IJobItemRepository
{
    private const int _exportCap = 100_000;

    private readonly RomdDbContext _context;

    public JobItemRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task AddRangeStagedAsync(IReadOnlyList<JobItem> items, CancellationToken ct = default)
    {
        var batch = items.ToArray();
        if (batch.Length == 0)
            return;
        var transaction = _context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A provenance batch requires the caller's transaction.");
        if (JobExecutionFenceScope.Current is not { } fence
            || batch.Any(item => item.JobId != fence.JobId)
            || _context.LockedJobExecution is not { } locked
            || locked.TransactionId != transaction.TransactionId
            || locked.JobId != fence.JobId || locked.Token != fence.Token)
            throw new JobExecutionOwnershipLostException();

        // Transaction entry verified the lease and holds the execution row until commit. Do not
        // reject an owned batch merely because its lease elapsed while takeover was blocked.
        var existing = _context.ChangeTracker.Entries<JobItemEntity>()
            .Select(entry => entry.Entity.Id).ToHashSet();
        foreach (var ids in batch.Select(item => item.Id).Distinct().Chunk(1000))
            existing.UnionWith(await _context.JobItems.Where(item => ids.Contains(item.Id))
                .Select(item => item.Id).ToListAsync(ct));
        _context.JobItems.AddRange(batch.Where(item => existing.Add(item.Id)).Select(JobItemEntity.FromDomain));
    }

    public async Task<JobItemPageResult> GetByJobAsync(
        Guid jobId,
        JobItemOutcome? outcome,
        int limit,
        CancellationToken ct = default,
        JobItemPosition? cursor = null,
        string? search = null)
    {
        var query = _context.JobItems.Where(i => i.JobId == jobId);

        if (cursor is not null)
        {
            query = query.Where(i => i.CreatedAt > cursor.CreatedAt || (i.CreatedAt == cursor.CreatedAt && i.Id.CompareTo(cursor.Id) > 0));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where(i => i.FileName.ToLower().Contains(term));
        }

        if (outcome.HasValue)
        {
            int outcomeValue = (int)outcome.Value;
            query = query.Where(i => i.Outcome == outcomeValue);
        }

        var entities = await query
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        bool hasMore = entities.Count > limit;
        if (hasMore)
        {
            entities = entities.Take(limit).ToList();
        }

        var views = await ResolveAsync(entities, ct);
        return new JobItemPageResult(views, hasMore, hasMore ? new JobItemPosition(entities[^1].CreatedAt, entities[^1].Id) : null);
    }

    public async Task<JobItem?> FindOutcomeAsync(Guid jobId, JobItemKind kind, string fileName, CancellationToken ct = default)
    {
        var id = JobItem.UploadIdentity(jobId, kind, fileName);
        var row = await _context.JobItems.SingleOrDefaultAsync(
            item => item.Id == id && item.JobId == jobId && item.Kind == (int)kind && item.FileName == fileName, ct);
        return row?.ToDomain();
    }

    public async Task AddStagedAsync(JobItem item, CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An upload outcome requires the content transaction.");
        if (JobExecutionFenceScope.Current is not { } fence || fence.JobId != item.JobId)
            throw new JobExecutionOwnershipLostException();
        if (_context.LockedJobExecution is not { } locked
            || locked.TransactionId != _context.Database.CurrentTransaction.TransactionId
            || locked.JobId != fence.JobId || locked.Token != fence.Token)
            throw new JobExecutionOwnershipLostException();
        if (item.Id != JobItem.UploadIdentity(item.JobId, item.Kind, item.FileName))
            throw new InvalidOperationException("The outcome does not have its stable upload identity.");
        string expectedPhase = item.Kind == JobItemKind.Rom ? "IngestingRoms" : "IngestingDats";
        var now = fence.Clock.GetUtcNow();
        int owned = await _context.Jobs.OfType<UploadJobEntity>().Where(job => job.Id == fence.JobId
                && job.ExecutionFenceToken == fence.Token
                && job.CompletedAt == null && job.Phase == expectedPhase)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.UpdatedAt, now), ct);
        if (owned != 1)
            throw new JobExecutionOwnershipLostException();
        // Ownership was checked at transaction entry. Its row lock still prevents takeover,
        // even if a long import outlives the lease while blocking heartbeat renewal.
        if (await FindOutcomeAsync(item.JobId, item.Kind, item.FileName, ct) is not null)
            throw new InvalidOperationException("The upload member already has a durable outcome.");
        _context.JobItems.Add(JobItemEntity.FromDomain(item));
    }

    public async Task<IReadOnlyList<JobItem>> GetOutcomesAsync(Guid jobId, CancellationToken ct = default)
    {
        var entities = await _context.JobItems.Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToListAsync(ct);
        return entities.Select(item => item.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<JobItemView>> GetAllByJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var entities = await _context.JobItems
            .Where(i => i.JobId == jobId)
            .OrderBy(i => i.CreatedAt)
            .Take(_exportCap)
            .ToListAsync(ct);

        return await ResolveAsync(entities, ct);
    }

    private async Task<IReadOnlyList<JobItemView>> ResolveAsync(
        List<JobItemEntity> entities,
        CancellationToken ct)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        var domains = entities.Select(e => e.ToDomain()).ToList();

        var platformIds = domains
            .Where(d => d.PlatformId.HasValue)
            .Select(d => d.PlatformId!.Value);

        var titleIds = domains
            .SelectMany(d => d.MatchedTitleIds);

        var platformNames = new Dictionary<int, string>();
        foreach (int[] platformIdBatch in BoundedIdQuery.DistinctBatches(platformIds))
        {
            var names = await _context.Platforms
                .Where(p => platformIdBatch.Contains(p.Id))
                .Select(p => new { p.Id, p.ShortName, p.Name })
                .ToListAsync(ct);

            foreach (var platform in names)
            {
                platformNames.Add(
                    platform.Id,
                    string.IsNullOrEmpty(platform.ShortName) ? platform.Name : platform.ShortName);
            }
        }

        var titleNames = new Dictionary<int, string>();
        foreach (int[] titleIdBatch in BoundedIdQuery.DistinctBatches(titleIds))
        {
            var names = await _context.Titles
                .Where(t => titleIdBatch.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(ct);

            foreach (var title in names)
            {
                titleNames.Add(title.Id, title.Name);
            }
        }

        return domains
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Select(d => new JobItemView
            {
                Id = d.Id,
                JobId = d.JobId,
                Kind = d.Kind,
                FileName = d.FileName,
                SizeBytes = d.SizeBytes,
                Outcome = d.Outcome,
                RomFileId = d.RomFileId,
                DatFileId = d.DatFileId,
                PlatformId = d.PlatformId,
                PlatformName = d.PlatformId is { } pid && platformNames.TryGetValue(pid, out var name)
                    ? name
                    : null,
                MatchedTitles = d.MatchedTitleIds
                    .Where(titleNames.ContainsKey)
                    .Select(id => new JobItemTitle(id, titleNames[id]))
                    .ToList(),
                GameCount = d.GameCount,
                ArchiveOnly = d.ArchiveOnly,
                Error = d.Error,
                CreatedAt = d.CreatedAt
            })
            .ToList();
    }
}
