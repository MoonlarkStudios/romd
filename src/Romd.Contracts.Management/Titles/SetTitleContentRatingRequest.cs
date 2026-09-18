using Romd.Contracts.Management.Models;

namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to set or clear the user content rating override for a single board.
///     A null or empty <see cref="Code" /> clears the override and reverts to the provider cascade.
/// </summary>
public sealed record SetTitleContentRatingRequest
{
    /// <summary>The rating board, supplied as its named string (e.g. "Esrb", "Pegi").</summary>
    public required RatingBoard Board { get; init; }

    /// <summary>
    ///     The canonical board code (e.g. "E10+", "PEGI 12"). Null or empty clears the override.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>Optional content descriptors to record alongside the rating.</summary>
    public IReadOnlyList<string>? Descriptors { get; init; }

    /// <summary>Optional rating synopsis.</summary>
    public string? Synopsis { get; init; }
}
