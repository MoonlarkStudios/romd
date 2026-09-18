namespace Romd.Contracts.Management.Models;

/// <summary>
///     A DAT entry that matches a ROM file in the library.
/// </summary>
public sealed record RomMatch
{
    public string? DatGameId { get; init; }
    public string? SystemKey { get; init; }
    public string? PlatformName { get; init; }
    /// <summary>
    ///     The DAT file ID.
    /// </summary>
    public required string DatId { get; init; }

    /// <summary>
    ///     The DAT file name.
    /// </summary>
    public required string DatName { get; init; }

    /// <summary>
    ///     The game name within the DAT.
    /// </summary>
    public required string GameName { get; init; }

    /// <summary>
    ///     The ROM entry name within the game.
    /// </summary>
    public required string RomName { get; init; }

    /// <summary>
    ///     The Title ID if the game is linked to a Title, or null if unrouted.
    /// </summary>
    public string? TitleId { get; init; }

    /// <summary>
    ///     The Title name if the game is linked to a Title, or null if unrouted.
    /// </summary>
    public string? TitleName { get; init; }
}
