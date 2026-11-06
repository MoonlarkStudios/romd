namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to move a single game to a different title.
/// </summary>
public sealed record MoveGameRequest
{
    /// <summary>
    ///     The Sqid of the existing title to move the game to.
    ///     Mutually exclusive with NewTitleName.
    /// </summary>
    public string? TargetTitleId { get; init; }

    /// <summary>
    ///     Name for a new title to create and move the game to.
    ///     Mutually exclusive with TargetTitleId.
    ///     If both are null, the game is unassigned from any title.
    /// </summary>
    public string? NewTitleName { get; init; }
}
