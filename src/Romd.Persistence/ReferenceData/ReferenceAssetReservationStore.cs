using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.ReferenceData.Assets;
using Romd.Domain.Hashing;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed class ReferenceAssetReservationStore(RomdDbContext db) : IReferenceAssetReservationStore
{
    private Guid? _lockedTransaction;

    public async Task AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Reference reservations require the caller's transaction.");
        await ReferenceCatalogService.LockAsync(db, ct);
        _lockedTransaction = transaction.TransactionId;
    }

    public async Task ReserveStagedAsync(Sha256 hash, int fileId, string contentType, DateTimeOffset reservedUntil,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is not { } transaction || _lockedTransaction != transaction.TransactionId)
            throw new InvalidOperationException("Reference reservations require the caller's catalog lock and transaction.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        if (hash.IsEmpty)
            throw new ArgumentException("A reference reservation requires a content hash.", nameof(hash));

        string key = hash.ToString();
        var asset = db.ReferenceAssets.Local.FirstOrDefault(row => row.Hash == key)
            ?? await db.ReferenceAssets.AsTracking().SingleOrDefaultAsync(row => row.Hash == key, ct);
        if (asset is null)
            db.ReferenceAssets.Add(new ReferenceAssetEntity
            {
                Hash = key,
                FileId = fileId,
                ContentType = contentType,
                ReservedUntil = reservedUntil
            });
        else
            asset.ReservedUntil = reservedUntil;
    }
}
