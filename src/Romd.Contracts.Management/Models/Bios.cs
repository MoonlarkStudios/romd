using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     A platform BIOS/firmware entry with derived ownership status.
/// </summary>
public sealed record Bios
{
    public required string Id { get; init; }
    public required string SystemKey { get; init; }
    public required string Name { get; init; }

    /// <summary>
    ///     Distinct required ROMs for this BIOS (deduplicated by hash across DAT revisions).
    /// </summary>
    public required int TotalRoms { get; init; }

    /// <summary>
    ///     Distinct required ROMs that are physically owned.
    /// </summary>
    public required int OwnedRoms { get; init; }

    /// <summary>
    ///     True when every required ROM is owned.
    /// </summary>
    public required bool IsOwned { get; init; }

    /// <summary>
    ///     Sum of DAT-declared sizes of the distinct required ROMs (the expectation).
    /// </summary>
    public required ByteCount RequiredBytes { get; init; }

    /// <summary>
    ///     Sum of DAT-declared sizes of the distinct required ROMs that are owned.
    /// </summary>
    public required ByteCount OwnedBytes { get; init; }

    /// <summary>
    ///     Sum of actual on-disk CAS sizes of the owned ROMs (the truth — what you hold).
    /// </summary>
    public required ByteCount OnDiskBytes { get; init; }
}
