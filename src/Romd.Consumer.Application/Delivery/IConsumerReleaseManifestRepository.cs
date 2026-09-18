using Romd.Domain.Hashing;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerReleaseManifestRequest(
    ConsumerLibraryScope Scope,
    int ReleaseId);

public sealed record ConsumerReleaseManifest(
    Guid UserId,
    int TitleId,
    int PlatformId,
    string PlatformShortName,
    int ReleaseId,
    string ReleaseName,
    string? Revision,
    bool IsComplete,
    ConsumerReleaseRuntime Runtime,
    IReadOnlyList<ConsumerReleaseManifestItem> Items);

public sealed record ConsumerReleaseRuntime(
    string ContentType,
    ConsumerLaunchTarget? Launch,
    string Packaging,
    long MinimumInstallBytes);

public sealed record ConsumerLaunchTarget(
    string Type,
    string RelativePath);

public sealed record ConsumerReleaseManifestItem(
    int? RomId,
    int? FileId,
    Sha256? Sha256,
    long SizeBytes,
    long? ContentSizeBytes,
    string RelativePath,
    string Role,
    bool IsAvailable);

public interface IConsumerReleaseManifestRepository
{
    Task<ConsumerLibraryReadResult<ConsumerReleaseManifest>> GetManifestAsync(
        ConsumerReleaseManifestRequest request,
        CancellationToken ct = default);
}
