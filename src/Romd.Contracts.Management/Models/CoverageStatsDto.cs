namespace Romd.Contracts.Management.Models;

public sealed record CoverageStatsDto
{
    public required int ExpectedTitleCount { get; init; }
    public required int LocalPayloadTitleCount { get; init; }
    public required int CompleteTitleCount { get; init; }
    public required int PartialTitleCount { get; init; }
    public required decimal CoverageHealthPercent { get; init; }
    public DateTimeOffset? LastDatRefreshAt { get; init; }
}
