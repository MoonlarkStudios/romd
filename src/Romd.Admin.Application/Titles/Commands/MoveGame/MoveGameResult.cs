using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.MoveGame;

/// <summary>
///     Result of moving a game to a title.
/// </summary>
public sealed record MoveGameResult(
    /// <summary>
    ///     The Sqid of the game that was moved.
    /// </summary>
    string GameId,

    /// <summary>
    ///     The title the game was moved to. Null if game was unassigned.
    /// </summary>
    Title? NewTitle,

    /// <summary>
    ///     True if a new title was created for this game.
    /// </summary>
    bool TitleCreated,

    /// <summary>
    ///     True if the old title was deleted (became empty).
    /// </summary>
    bool OldTitleDeleted
);
