using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Hashing;

/// <summary>
///     Complete hash bundle for content identification.
///     SHA-256 is the authoritative identity; other hashes enable DAT compatibility.
/// </summary>
public readonly record struct ContentHash
{
    /// <summary>
    ///     Primary content identifier. Always present for stored content.
    /// </summary>
    public required Sha256 Sha256 { get; init; }

    /// <summary>
    ///     SHA-1 hash for DAT matching. Most DAT files provide this.
    /// </summary>
    public Sha1 Sha1 { get; init; }

    /// <summary>
    ///     MD5 hash for legacy DAT compatibility.
    /// </summary>
    public Md5 Md5 { get; init; }

    /// <summary>
    ///     CRC32 checksum for legacy DAT compatibility.
    /// </summary>
    public Crc32 Crc32 { get; init; }

    /// <summary>
    ///     True if this hash has a valid SHA-256 identifier.
    /// </summary>
    public bool IsValid => !Sha256.IsEmpty;

    /// <summary>
    ///     True if all four hashes are present.
    /// </summary>
    public bool IsComplete =>
        !Sha256.IsEmpty && !Sha1.IsEmpty && !Md5.IsEmpty && !Crc32.IsEmpty;
}
