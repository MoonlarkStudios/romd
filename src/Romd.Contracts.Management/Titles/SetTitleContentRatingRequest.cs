namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to set the user content rating override for the board named in the route. The override is
///     cleared with <c>DELETE</c> on the same route, which reverts the board to the provider cascade.
/// </summary>
public sealed record SetTitleContentRatingRequest
{
    /// <summary>The canonical board code (e.g. "E10+", "PEGI 12").</summary>
    public required string Code { get; init; }

    /// <summary>Optional content descriptors to record alongside the rating.</summary>
    public IReadOnlyList<string>? Descriptors { get; init; }

    /// <summary>Optional rating synopsis.</summary>
    public string? Synopsis { get; init; }
}
