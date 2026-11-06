using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerReleaseDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Revision { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required ByteCount SizeBytes { get; init; }
    public required bool IsComplete { get; init; }
}
