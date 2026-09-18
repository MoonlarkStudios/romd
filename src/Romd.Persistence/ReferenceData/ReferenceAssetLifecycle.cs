using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed partial class ReferenceCatalogService
{
    public Task<int> ReleaseExpiredAssetsAsync(DateTimeOffset now, CancellationToken ct) => ReleaseExpiredAssetsAsync(db, now, ct);

    /// <summary>Release expired reference reservations. The existing CAS collector deletes unowned files afterwards.</summary>
    public static async Task<int> ReleaseExpiredAssetsAsync(RomdDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var count = await db.ReferenceAssets.Where(asset => asset.ReservedUntil < now &&
            !db.ReferenceAssetOwners.Any(owner => owner.Hash == asset.Hash)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
