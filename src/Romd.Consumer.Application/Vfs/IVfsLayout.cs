using ErrorOr;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

/// <summary>
/// A code-defined namespace. Paths are decoded, layout-relative paths, never URLs.
/// Every read must use current library access; results are not durable access grants.
/// Implementations must bound enumeration and observe cancellation.
/// </summary>
public interface IVfsLayout
{
    string Key { get; }

    /// <summary>Directory reads. File entries here are metadata only; content is never opened from them.</summary>
    Task<ErrorOr<VfsLayoutRead>> ReadAsync(
        ConsumerLibraryScope scope, string path, bool includeChildren,
        int maximumChildren, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored);

    /// <summary>Maps a path that may name a release file; the service performs the scoped lookup.</summary>
    ConsumerVfsFileLocator? LocateFile(string path);

    /// <summary>
    /// The canonical layout path of a resolved file, compared exactly with the requested path,
    /// or null when this layout cannot project the file's release under its own path rules.
    /// </summary>
    string? FilePath(ConsumerVfsResolvedFile file);
}

public sealed record ConsumerVfsFileLocator(int ReleaseId, string RelativePath);

/// <summary>A bounded metadata snapshot; does not open content streams.</summary>
public sealed record VfsLayoutRead(VfsLayoutEntry Entry, IReadOnlyList<VfsLayoutEntry> Children);

public sealed record VfsLayoutEntry(string Path, string Name, ConsumerVfsFile? File = null)
{
    public bool IsDirectory => File is null;
}
