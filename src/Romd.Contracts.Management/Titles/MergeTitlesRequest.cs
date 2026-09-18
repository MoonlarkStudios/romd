namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to merge two titles. Source title's games move to target,
///     metadata is merged, and source is deleted.
/// </summary>
public sealed record MergeTitlesRequest
{
    /// <summary>
    ///     The Sqid of the title to merge FROM (will be deleted).
    /// </summary>
    public required string SourceTitleId { get; init; }

    /// <summary>
    ///     The Sqid of the title to merge INTO (will be kept).
    /// </summary>
    public required string TargetTitleId { get; init; }
}
