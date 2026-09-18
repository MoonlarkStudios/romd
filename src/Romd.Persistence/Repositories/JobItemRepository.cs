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

    public async Task AddRangeAsync(IReadOnlyList<JobItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Provenance is a worker checkpoint, not a catalog mutation. Own a short fenced
        // transaction when called by a claimed runner so a stale sink cannot publish records
        // that later authorize move-mode source deletion.
        await using var transaction = JobExecutionFenceScope.Current is not null
            && _context.Database.CurrentTransaction is null
            ? await new EfUnitOfWork(_context).BeginTransactionAsync(ct)
            : null;
        _context.JobItems.AddRange(items.Select(JobItemEntity.FromDomain));
        await _context.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    public async Task<JobItemPageResult> GetByJobAsync(
        Guid jobId,
        JobItemOutcome? outcome,
        int limit,
        CancellationToken ct = default,
        Guid? cursor = null,
        string? search = null)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var query = _context.JobItems.Where(i => i.JobId == jobId);

        if (cursor.HasValue)
        {
            var anchor = await _context.JobItems.FirstOrDefaultAsync(i => i.JobId == jobId && i.Id == cursor.Value, ct);
            if (anchor is null)
                return new JobItemPageResult([], false);
            query = query.Where(i => i.CreatedAt > anchor.CreatedAt || (i.CreatedAt == anchor.CreatedAt && i.Id.CompareTo(anchor.Id) > 0));
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
        return new JobItemPageResult(views, hasMore, hasMore ? entities[^1].Id.ToString() : null);
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
