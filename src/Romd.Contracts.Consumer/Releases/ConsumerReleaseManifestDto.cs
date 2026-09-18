using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerReleaseManifestDto
{
    public required string ServerInstanceId { get; init; }
    public required string ReleaseId { get; init; }
    public required string TitleId { get; init; }
    public required string SystemKey { get; init; }
    public required string Name { get; init; }
    public string? Revision { get; init; }
    public required bool IsComplete { get; init; }
    public required ConsumerReleaseRuntimeDto Runtime { get; init; }
    public required IReadOnlyList<ConsumerReleaseManifestItemDto> Items { get; init; }
}

public sealed record ConsumerReleaseRuntimeDto
{
    public required string ContentType { get; init; }
    public ConsumerLaunchTargetDto? Launch { get; init; }
    public required string Packaging { get; init; }
    public required ByteCount MinimumInstallBytes { get; init; }
}

public sealed record ConsumerLaunchTargetDto
{
    public required string Type { get; init; }
    public required string RelativePath { get; init; }
}

public sealed record ConsumerReleaseManifestItemDto
{
    public required string RelativePath { get; init; }
    public required string Role { get; init; }
    public required ByteCount SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public required bool IsAvailable { get; init; }
    public ContentGrantDto? ContentGrant { get; init; }
}

public sealed record ContentGrantDto
{
    public required string DownloadUrl { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
