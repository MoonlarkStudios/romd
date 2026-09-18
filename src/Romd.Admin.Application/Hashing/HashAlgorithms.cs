namespace Romd.Admin.Application.Hashing;

/// <summary>
///     Flags specifying which hash algorithms to compute.
/// </summary>
[Flags]
public enum HashAlgorithms
{
    /// <summary>
    ///     No hashes.
    /// </summary>
    None = 0,

    /// <summary>
    ///     SHA-256 (primary content identifier for CAS).
    /// </summary>
    Sha256 = 1 << 0,

    /// <summary>
    ///     SHA-1 (primary DAT hash for most catalogs).
    /// </summary>
    Sha1 = 1 << 1,

    /// <summary>
    ///     MD5 (legacy DAT compatibility).
    /// </summary>
    Md5 = 1 << 2,

    /// <summary>
    ///     CRC32 (legacy DAT compatibility).
    /// </summary>
    Crc32 = 1 << 3,

    /// <summary>
    ///     All hashes used for DAT matching (SHA-1, MD5, CRC32).
    /// </summary>
    DatHashes = Sha1 | Md5 | Crc32,

    /// <summary>
    ///     All four hashes.
    /// </summary>
    All = Sha256 | Sha1 | Md5 | Crc32
}
