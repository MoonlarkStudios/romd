using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Persistence.Subscriptions;

public sealed partial class DatCatalogEnrollmentService : IDatSubscriptionApplyStore
{
    public async Task<DatSubscriptionApplyPlatform?> ReadPlatformAsync(int platformId, CancellationToken ct = default)
    {
        RequireApplyTransaction(ct);
        if (!await _db.Platforms.AnyAsync(platform => platform.Id == platformId, ct))
            return null;
        var key = await _db.Platforms.Where(platform => platform.Id == platformId).Select(platform => platform.CanonicalKey).SingleAsync(ct);
        var definition = await _db.Platforms.Where(platform => platform.CanonicalKey == key && platform.Ownership != null)
            .Select(platform => new { platform.Retired }).SingleOrDefaultAsync(ct);
        return new(definition is not null, definition?.Retired ?? false);
    }

    public async Task StageAcceptedAsync(int id, int? sourceId, Guid jobId, CancellationToken ct = default)
    {
        RequireApplyTransaction(ct);
        var row = await _db.DatSubscriptions.AsTracking().SingleAsync(row => row.Id == id, ct);
        row.DatSourceId = sourceId;
        row.JobId = jobId;
        row.State = "Applying";
        row.Message = null;
    }

    private void RequireApplyTransaction(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Subscription application requires a caller-owned transaction.");
    }
}
