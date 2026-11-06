namespace Romd.Contracts.Management.TrackedCollection;

public sealed record TrackedCollectionStatsDto
{
    public required int TrackedTitleCount { get; init; }
    public required int SatisfiedTitleCount { get; init; }
    public required int MissingTitleCount { get; init; }
    public required int UpgradeTitleCount { get; init; }
    public required decimal CompletionPercent { get; init; }
    public required IReadOnlyList<TrackedCollectionPlatformStatsDto> Platforms { get; init; }
}

public sealed record TrackedCollectionPlatformStatsDto
{
    public required string SystemKey { get; init; }
    public required string PlatformName { get; init; }
    public required int TrackedTitleCount { get; init; }
    public required int SatisfiedTitleCount { get; init; }
    public required int MissingTitleCount { get; init; }
    public required int UpgradeTitleCount { get; init; }
    public required decimal CompletionPercent { get; init; }
}
