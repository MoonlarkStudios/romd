namespace Romd.Admin.Application.TrackedCollection.ReadModels;

public sealed record TrackedCollectionTitleData
{
    public required int TitleId { get; init; }
    public required int PlatformId { get; init; }
    public required string PlatformName { get; init; }
    public required string TitleName { get; init; }
    public required bool IsSatisfied { get; init; }
    public required bool HasUpgrade { get; init; }
    public required bool IsPinned { get; init; }
    public DateTimeOffset? SatisfiedAt { get; init; }
    public TrackedCollectionReleaseData? DesiredRelease { get; init; }
    public TrackedCollectionReleaseData? OwnedRelease { get; init; }
}

public sealed record TrackedCollectionReleaseData(
    int CatalogReleaseId,
    string Name,
    string? Region,
    string? Revision);

public sealed record TrackedCollectionStatsData
{
    public required int TrackedTitleCount { get; init; }
    public required int SatisfiedTitleCount { get; init; }
    public required int MissingTitleCount { get; init; }
    public required int UpgradeTitleCount { get; init; }
    public required decimal CompletionPercent { get; init; }
    public required IReadOnlyList<TrackedCollectionPlatformStatsData> Platforms { get; init; }
}

public sealed record TrackedCollectionPlatformStatsData
{
    public required int PlatformId { get; init; }
    public required string PlatformName { get; init; }
    public required int TrackedTitleCount { get; init; }
    public required int SatisfiedTitleCount { get; init; }
    public required int MissingTitleCount { get; init; }
    public required int UpgradeTitleCount { get; init; }
    public required decimal CompletionPercent { get; init; }
}
