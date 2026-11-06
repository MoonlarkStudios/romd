using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Contracts.Consumer.Delivery;

public sealed record ConsumerPlatformBiosDto
{
    public required string SystemKey { get; init; }
    public required IReadOnlyList<ConsumerPlatformBiosItemDto> Items { get; init; }
}

public sealed record ConsumerPlatformBiosItemDto
{
    public required string BiosId { get; init; }
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required ByteCount SizeBytes { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Sha256 { get; init; }
    public required bool IsAvailable { get; init; }
    public ContentGrantDto? ContentGrant { get; init; }
}
