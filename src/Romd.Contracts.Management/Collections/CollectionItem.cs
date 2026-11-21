namespace Romd.Contracts.Management.Collections;

public sealed record CollectionItem
{
    public required string TitleId { get; init; }
    public required string TitleName { get; init; }
    public required string SystemKey { get; init; }
    /// <summary>Legacy cover media available for use as the collection's artwork.</summary>
    public string? CoverUrl { get; init; }
    /// <summary>The title's resolved poster, including its configured artwork selection.</summary>
    public string? PosterUrl { get; init; }
    public string? Note { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}
