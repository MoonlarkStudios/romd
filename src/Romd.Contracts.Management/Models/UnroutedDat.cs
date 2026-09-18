namespace Romd.Contracts.Management.Models;

/// <summary>
///     An unrouted DAT with the number of stored ROM files waiting on it.
/// </summary>
public sealed record UnroutedDat
{
    public required Dat Dat { get; init; }

    /// <summary>
    ///     Distinct stored ROM files matched to this DAT's entries —
    ///     they catalog as soon as the DAT is routed to a system.
    /// </summary>
    public required int MatchedRomFileCount { get; init; }
}
