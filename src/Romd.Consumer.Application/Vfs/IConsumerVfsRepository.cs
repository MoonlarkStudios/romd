using Romd.Consumer.Application.Libraries;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

public sealed record ConsumerVfsPlatform(int Id, string Name);

public sealed record ConsumerVfsPlatformPage(
    IReadOnlyList<ConsumerVfsPlatform> Items, int? NextPlatformId);

public sealed record ConsumerVfsReleaseSummary(int Id, int PlatformId, string Name, string PlatformName, string? SystemKey = null);

/// <summary>Projected paths per listed release; layouts apply their own path rules.</summary>
public sealed record ConsumerVfsReleasePage(
    IReadOnlyList<ConsumerVfsReleaseSummary> Items, int? NextReleaseId,
    IReadOnlyDictionary<int, IReadOnlyList<string>> ReleasePaths);

/// <summary>A resolved file with every projected path in its release, for layout path rules.</summary>
public sealed record ConsumerVfsResolvedFile(ConsumerVfsReleaseSummary Summary, ConsumerVfsFile File,
    IReadOnlyList<string> ReleasePaths);

public sealed record ConsumerVfsRelease(
    ConsumerVfsReleaseSummary Summary, ConsumerVfsReleaseTree Tree);

/// <summary>
/// Reads the user's current published library in one database snapshot per call.
/// Results are metadata snapshots, not durable content access grants.
/// </summary>
public interface IConsumerVfsRepository
{
    const int MaximumPageSize = 200;

    Task<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>> ListPlatformsAsync(
        ConsumerLibraryScope scope, int afterPlatformId = 0, int limit = 100, CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<ConsumerVfsPlatform>> GetPlatformAsync(
        ConsumerLibraryScope scope, int platformId, CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<ConsumerVfsReleasePage>> ListReleasesAsync(
        ConsumerLibraryScope scope, int platformId, int afterReleaseId = 0, int limit = 100, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored);

    Task<ConsumerLibraryReadResult<ConsumerVfsRelease>> GetReleaseAsync(
        ConsumerLibraryScope scope, int releaseId, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored);
    Task<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>> GetFileAsync(
        ConsumerLibraryScope scope, int releaseId, string relativePath, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored);
}
