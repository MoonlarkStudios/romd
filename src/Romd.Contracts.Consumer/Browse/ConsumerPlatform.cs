namespace Romd.Contracts.Consumer.Browse;

public sealed record ConsumerPlatformSummary
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public string? CoverUrl { get; init; }
}

public sealed record ConsumerPlatformDetail
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public required IReadOnlyList<ConsumerMediaRef> Media { get; init; }
}
