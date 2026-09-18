namespace Romd.Contracts.Management.Models;

public sealed record SystemHealthDto
{
    public required int TotalRoms { get; init; }
    public required int IdentifiedCount { get; init; }
    public required int UnidentifiedCount { get; init; }
    public required int RoutedCount { get; init; }
    public required int UnroutedCount { get; init; }
    public required int FailedJobCount { get; init; }
}
