namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to update user-authored metadata for a title.
///     Null values mean "remove user override for this field" and fall back to provider data.
///     Empty strings clear the field entirely.
/// </summary>
public sealed record UpdateTitleMetadataRequest
{
    /// <summary>
    ///     Override the display name. Null removes user override.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Override the description. Null removes user override.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Override the publisher. Null removes user override.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>
    ///     Override the developer. Null removes user override.
    /// </summary>
    public string? Developer { get; init; }

    /// <summary>
    ///     Override the genre. Null removes user override.
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    ///     Override the release date. Null removes user override.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>
    ///     Override the player count. Null removes user override.
    /// </summary>
    public int? Players { get; init; }

    /// <summary>
    ///     Override the rating. Null removes user override.
    /// </summary>
    public double? Rating { get; init; }

}
