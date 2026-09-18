using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     The stored CAS blob backing a resource (a DAT's source file, a media asset, etc.).
///     Null on the parent when no blob is present. Sizes come from the content-addressable store,
///     so on-disk size is the source of truth for actual bytes held; the logical size is the
///     uncompressed expectation.
/// </summary>
public sealed record StoredFileRef
{
    /// <summary>Logical (uncompressed) size in bytes.</summary>
    public required ByteCount SizeBytes { get; init; }

    /// <summary>Actual on-disk size in bytes (possibly compressed).</summary>
    public required ByteCount SizeOnDiskBytes { get; init; }

    /// <summary>Whether the blob is stored compressed on disk.</summary>
    public required bool IsCompressed { get; init; }
}
