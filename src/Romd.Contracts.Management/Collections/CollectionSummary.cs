namespace Romd.Contracts.Management.Collections;

public sealed record CollectionSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? SystemKey { get; init; }
    public string? CoverUrl { get; init; }
    public int ItemCount { get; init; }
    public bool IsSystem { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
