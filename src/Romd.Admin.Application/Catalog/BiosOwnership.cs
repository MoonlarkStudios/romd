namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Derived ownership view of a BIOS entry: how many of its distinct required ROMs
///     (deduplicated by SHA-1 across DAT revisions) are physically owned, and the byte totals.
///     Computed on read from existing ROM links — never stored.
/// </summary>
/// <param name="RequiredBytes">Sum of DAT-declared sizes of the distinct required ROMs (the expectation).</param>
/// <param name="OwnedBytes">Sum of DAT-declared sizes of the distinct required ROMs that are owned.</param>
/// <param name="OnDiskBytes">Sum of actual on-disk CAS sizes of the owned ROMs (the truth).</param>
public sealed record BiosOwnership(
    int BiosId,
    int PlatformId,
    string Name,
    int TotalRoms,
    int OwnedRoms,
    long RequiredBytes,
    long OwnedBytes,
    long OnDiskBytes)
{
    /// <summary>
    ///     True when every distinct required ROM is physically present.
    /// </summary>
    public bool IsOwned => TotalRoms > 0 && OwnedRoms == TotalRoms;
}
