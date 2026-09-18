namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to set the tracked state of many titles at once.
/// </summary>
public sealed record BatchSetTitleTrackedRequest
{
    /// <summary>
    ///     Sqid-encoded title IDs to update.
    /// </summary>
    public IReadOnlyList<string>? TitleIds { get; init; }

    /// <summary>
    ///     Sqid-encoded platform whose titles should be updated.
    /// </summary>
    public string? SystemKey { get; init; }

    /// <summary>
    ///     Sqid-encoded stable DAT source whose associated titles should be updated.
    /// </summary>
    public string? DatSourceId { get; init; }

    /// <summary>
    ///     True to track the titles, false to untrack them.
    /// </summary>
    public required bool Tracked { get; init; }
}
