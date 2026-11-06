namespace Romd.Contracts.Consumer.Browse;

public sealed record ConsumerPlatformSummaryDto
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public string? CoverUrl { get; init; }
}

public sealed record ConsumerPlatformDetailDto
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public required IReadOnlyList<ConsumerMediaRefDto> Media { get; init; }
}
