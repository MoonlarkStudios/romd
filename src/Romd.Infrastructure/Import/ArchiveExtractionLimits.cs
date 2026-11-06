namespace Romd.Infrastructure.Import;

/// <summary>
///     Caps and job-wide budgets for recursive archive extraction inside one import job. Defaults
///     preserve the historical limits; tests inject smaller budgets. <see cref="MaxTotalEntries" />
///     mirrors the import-wide file ceiling so extraction cannot multiply it across archives.
/// </summary>
public sealed record ArchiveExtractionLimits
{
    public int MaxNestingDepth { get; init; } = 8;
    public int MaxTotalEntries { get; init; } = ImportPreflight.MaxFileCount;
    public int MaxPerArchiveEntries { get; init; } = 50_000;
    public long MaxExpansionRatio { get; init; } = 200;
    public long SpaceFloorBytes { get; init; } = 1L << 30;
}
