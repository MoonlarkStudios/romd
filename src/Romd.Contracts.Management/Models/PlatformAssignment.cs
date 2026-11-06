namespace Romd.Contracts.Management.Models;

/// <summary>
///     Result of a platform assignment operation.
/// </summary>
public sealed record PlatformAssignment
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
