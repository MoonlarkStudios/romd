using Romd.Domain.Hashing;

namespace Romd.Application.Common.Content;

/// <summary>Read only after release authorization, within the caller's consistent database snapshot.</summary>
public interface IOriginalDiscLayoutReader
{
    /// <summary>Null means the binding is inconsistent; an identity with no Id means no accepted attachment.</summary>
    Task<OriginalDiscLayoutIdentity?> ReadIdentityAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct);
    Task<bool> HasStaleAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct) => Task.FromResult(false);
    Task<OriginalDiscLayout?> ResolveAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct);
}

/// <summary>Authoritative attachment decision, independent of whether its original bytes are currently available.</summary>
public sealed record OriginalDiscLayoutIdentity(int? Id, Guid? Revision);

/// <summary>Exact pinned original bytes. Not an authorization capability.</summary>
public sealed record OriginalDiscLayout(int Id, Guid Revision, int PlatformId, int ReleaseId,
    string Fingerprint, string EntryPoint, IReadOnlyList<OriginalDiscLayoutFile> Files);

/// <param name="RomId">Null for the separately retained descriptor; dependencies are catalog-matched originals.</param>
public sealed record OriginalDiscLayoutFile(string Path, int FileId, Sha256 Sha256, long SizeBytes, int? RomId, string Role);
