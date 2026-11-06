using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Enrichment;

/// <summary>Reads and acknowledges internal per-title intents, separately from title mutations.</summary>
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
                request.Id, request.TitleId, request.Attempts))
            .ToListAsync(ct);

    public Task AcknowledgeAsync(Guid requestId, CancellationToken ct = default) =>
        _context.MetadataRematerializationRequests.Where(row => row.Id == requestId).ExecuteDeleteAsync(ct);

    public Task RetryAsync(Guid requestId, DateTimeOffset retryAt, CancellationToken ct = default) =>
        _context.MetadataRematerializationRequests.Where(row => row.Id == requestId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Attempts, row => row.Attempts + 1)
                .SetProperty(row => row.AvailableAtUtc, retryAt), ct);

}
