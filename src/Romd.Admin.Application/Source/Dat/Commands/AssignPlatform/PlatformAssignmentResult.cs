namespace Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;

/// <summary>
///     Result of assigning a platform to a DAT file.
/// </summary>
public sealed record PlatformAssignmentResult
{
    /// <summary>
    ///     Number of games that were updated with TitleIds.
    /// </summary>
    public required int GamesUpdated { get; init; }

    /// <summary>
    ///     Number of new Titles that were created.
    /// </summary>
    public required int NewTitlesCreated { get; init; }

    /// <summary>
    ///     Number of games that matched existing Titles.
    /// </summary>
    public required int ExistingTitlesMatched { get; init; }
}
