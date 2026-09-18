using Romd.Contracts.Management.Models;

namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Response from moving a game to a title.
/// </summary>
public sealed record MoveGameResponse
{
    /// <summary>
    ///     The Sqid of the game that was moved.
    /// </summary>
    public required string GameId { get; init; }

    /// <summary>
    ///     The title the game was moved to. Null if unassigned.
    /// </summary>
    public Title? NewTitle { get; init; }

    /// <summary>
    ///     True if a new title was created.
    /// </summary>
    public bool TitleCreated { get; init; }

    /// <summary>
    ///     True if the old title was deleted (became empty).
    /// </summary>
    public bool OldTitleDeleted { get; init; }
}
