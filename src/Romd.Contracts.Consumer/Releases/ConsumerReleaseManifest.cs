using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerReleaseManifest
{
    public required string ServerInstanceId { get; init; }
    public required string ReleaseId { get; init; }
    public required string TitleId { get; init; }
    public required string SystemKey { get; init; }
    public required string Name { get; init; }
    public string? Revision { get; init; }
    public required bool IsComplete { get; init; }
    public required bool IsDeliveryComplete { get; init; }
    public required ConsumerReleaseRuntime Runtime { get; init; }
    public required IReadOnlyList<ConsumerReleaseManifestItem> Items { get; init; }
}

public sealed record ConsumerReleaseRuntime
{
    public required string ContentType { get; init; }
    public ConsumerLaunchTarget? Launch { get; init; }
    public required string Packaging { get; init; }
    public required ByteCount MinimumInstallBytes { get; init; }
}

public sealed record ConsumerLaunchTarget
{
    public required string Type { get; init; }
    public required string RelativePath { get; init; }
}

public enum ConsumerReleaseFileProvenance { Original, Derived }

public sealed record ConsumerReleaseManifestItem
{
    public required string RelativePath { get; init; }
    public required string Role { get; init; }
    public required ConsumerReleaseFileProvenance Provenance { get; init; }
    public required ByteCount SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public required bool IsAvailable { get; init; }
    public ContentGrant? ContentGrant { get; init; }
    public required IReadOnlyList<ConsumerReleaseManifestFormat> Formats { get; init; }
}

public sealed record ConsumerReleaseManifestFormat : RomDeliveryFormatMetadata
{
    public required string RelativePath { get; init; }
    public ConsumerReleaseRuntime? Runtime { get; init; }
    public ContentGrant? ContentGrant { get; init; }
}

public sealed record ContentGrant
{
    public required string DownloadUrl { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
