using Romd.Domain.Hashing;

namespace Romd.Admin.Application.ReferenceData.Assets;

/// <summary>Stages reference-asset reservations inside the caller's transaction and catalog lock.</summary>
public interface IReferenceAssetReservationStore
{
    /// <summary>Acquires the reference mutation lock before the caller acquires any file locks.</summary>
    Task AcquireAsync(CancellationToken ct);

    /// <summary>
    ///     Reserves a published file or renews only the deadline of an existing reservation.
    ///     The caller owns identity flushing and commit; this method never flushes staged changes.
    /// </summary>
    Task ReserveStagedAsync(Sha256 hash, int fileId, string contentType, DateTimeOffset reservedUntil,
        CancellationToken ct);
}
