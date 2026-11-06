using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Enrichment;

/// <summary>Stages work in the caller's transaction; the worker reconciles committed requests.</summary>
public sealed class RematerializationScheduler : IRematerializationScheduler
{
    private readonly RomdDbContext _context;
    private readonly TimeProvider _clock;

    public RematerializationScheduler(RomdDbContext context, TimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public Task EnqueueTitleAsync(int titleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Metadata rematerialization intent requires a caller-owned transaction.");

        var now = _clock.GetUtcNow();
        _context.MetadataRematerializationRequests.Add(new MetadataRematerializationRequestEntity
        {
            TitleId = titleId,
            CreatedAtUtc = now,
            AvailableAtUtc = now
        });
        return Task.CompletedTask;
    }
}
