namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Partial update of one title media item. Send exactly one member: <c>{ "isPrimary": true }</c> makes the item
///     the primary for its type, demoting the previous primary; <see cref="Classification" /> replaces the item's
///     type and placement as a whole.
/// </summary>
public sealed record UpdateTitleMediaRequest
{
    public bool? IsPrimary { get; init; }

    public TitleMediaClassification? Classification { get; init; }
}

/// <summary>
///     The whole classification of one media item. A null <see cref="CatalogReleaseId" /> places it at title level.
///     Only uploads and manually collected media can change type, and images and manuals never exchange types.
///     <see cref="IsPreferred" /> applies to packaging scans and displaces the previous preference of the same type
///     and release.
/// </summary>
public sealed record TitleMediaClassification
{
    public required string Type { get; init; }
    public string? CatalogReleaseId { get; init; }
    public string? Region { get; init; }
    public string? Language { get; init; }
    public bool IsPreferred { get; init; }
}
