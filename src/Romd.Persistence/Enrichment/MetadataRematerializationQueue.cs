using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Enrichment;

/// <summary>Owns the queue's short fanout transaction, separately from application mutations.</summary>
public sealed class MetadataRematerializationQueue : IMetadataRematerializationQueue
{
    private readonly RomdDbContext _context;

    public MetadataRematerializationQueue(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<MetadataRematerializationRequest>> GetPendingAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default) =>
        await _context.MetadataRematerializationRequests.AsNoTracking()
            .Where(request => request.AvailableAtUtc <= now)
            .OrderBy(request => request.AvailableAtUtc).ThenBy(request => request.Id)
            .Take(limit)
            .Select(request => new MetadataRematerializationRequest(
                request.Id, request.TitleId, request.PlatformId, request.Attempts))
            .ToListAsync(ct);

    public Task AcknowledgeAsync(Guid requestId, CancellationToken ct = default) =>
        _context.MetadataRematerializationRequests.Where(row => row.Id == requestId).ExecuteDeleteAsync(ct);

    public Task RetryAsync(Guid requestId, DateTimeOffset retryAt, CancellationToken ct = default) =>
        _context.MetadataRematerializationRequests.Where(row => row.Id == requestId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Attempts, row => row.Attempts + 1)
                .SetProperty(row => row.AvailableAtUtc, retryAt), ct);

    public async Task ExpandAsync(
        MetadataRematerializationRequest request,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        const int batchSize = 500;
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        try
        {
            // Deleting first locks this parent until commit. A competing expansion cannot
            // duplicate children, and rollback restores the parent together with all batches.
            int deleted = await _context.MetadataRematerializationRequests
                .Where(row => row.Id == request.Id)
                .ExecuteDeleteAsync(ct);
            if (deleted == 0)
                return;

            var titles = _context.Titles.AsNoTracking();
            if (request.PlatformId is { } platformId)
                titles = titles.Where(title => title.PlatformId == platformId);

            int lastTitleId = 0;
            while (true)
            {
                var ids = await titles.Where(title => title.Id > lastTitleId)
                    .OrderBy(title => title.Id)
                    .Select(title => title.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);
                if (ids.Count == 0)
                    break;

                var children = ids.Select(titleId => new MetadataRematerializationRequestEntity
                {
                    TitleId = titleId,
                    CreatedAtUtc = now,
                    AvailableAtUtc = now
                }).ToList();
                _context.MetadataRematerializationRequests.AddRange(children);
                await _context.SaveChangesAsync(ct);
                foreach (var child in children)
                    _context.Entry(child).State = EntityState.Detached;

                lastTitleId = ids[^1];
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            // This worker's dedicated read/dispatch context owns no caller state. In particular,
            // failed batches must not remain staged for an unrelated request's later save.
            _context.ChangeTracker.Clear();
        }
    }

}
