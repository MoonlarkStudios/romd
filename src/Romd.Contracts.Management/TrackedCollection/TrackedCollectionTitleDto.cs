namespace Romd.Contracts.Management.TrackedCollection;

public sealed record TrackedCollectionTitleDto
{
    public required string TitleId { get; init; }
    public required string SystemKey { get; init; }
    public required string PlatformName { get; init; }
    public required string TitleName { get; init; }
    public required bool IsSatisfied { get; init; }
    public required bool HasUpgrade { get; init; }
    public required bool IsPinned { get; init; }
    public DateTimeOffset? SatisfiedAt { get; init; }
    public TrackedCollectionReleaseDto? DesiredRelease { get; init; }
    public TrackedCollectionReleaseDto? OwnedRelease { get; init; }
}

public sealed record TrackedCollectionReleaseDto
{
    public required string CatalogReleaseId { get; init; }
    public required string Name { get; init; }
    public string? Region { get; init; }
    public string? Revision { get; init; }
}
