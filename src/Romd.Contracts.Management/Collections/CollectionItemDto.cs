namespace Romd.Contracts.Management.Collections;

public sealed record CollectionItemDto
{
    public required string TitleId { get; init; }
    public required string TitleName { get; init; }
    public required string SystemKey { get; init; }
    public string? CoverUrl { get; init; }
    public string? Note { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}
