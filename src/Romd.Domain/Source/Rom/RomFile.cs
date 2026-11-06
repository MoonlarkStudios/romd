using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>
///     Represents a managed ROM file in the system.
///     Hashes and Size identify logical ROM bytes; FileId identifies the preferred stored artifact.
///     An artifact may encode those bytes, and unmatched content may be retained by policy.
/// </summary>
public sealed class RomFile
{
    private RomFile(
        int id,
        string originalFilename,
        int fileId,
        long size,
        Sha1 sha1,
        Md5 md5,
        Crc32 crc32,
        DateTimeOffset importedAt)
    {
        Id = id;
        OriginalFilename = originalFilename;
        FileId = fileId;
        Size = size;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        ImportedAt = importedAt;
    }

    public int Id { get; private set; }

    /// <summary>
    ///     The delivery filename derived from the first import (RVZ imports use an .iso extension).
    ///     Used when exporting the file to give it a meaningful name.
    /// </summary>
    public string OriginalFilename { get; private set; }

    public int FileId { get; private set; }

    public Sha256? ContentSha256 { get; private set; }
    public RomRepresentationEncoding StorageEncoding { get; private set; }

    /// <summary>
    ///     File size in bytes.
    /// </summary>
    public long Size { get; private set; }

    /// <summary>
    ///     SHA-1 of logical ROM bytes, used for DAT matching. CAS uses the artifact's SHA-256.
    /// </summary>
    public Sha1 Sha1 { get; private set; }

    /// <summary>
    ///     MD5 hash - used by some DATs.
    /// </summary>
    public Md5 Md5 { get; private set; }

    /// <summary>
    ///     CRC32 hash - used by some DATs and emulators.
    /// </summary>
    public Crc32 Crc32 { get; private set; }

    /// <summary>
    ///     When this file was added to the library.
    /// </summary>
    public DateTimeOffset ImportedAt { get; private set; }

    /// <summary>
    ///     Creates a new ROM file entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when originalFilename is null or whitespace.</exception>
    public static RomFile CreateNew(
        string originalFilename,
        int fileId,
        long size,
        Sha256 contentSha256,
        Sha1 sha1,
        Md5 md5,
        Crc32 crc32,
        DateTimeOffset now)
    {
        return CreateRepresented(originalFilename, fileId, size, contentSha256, sha1, md5, crc32, RomRepresentationEncoding.Identity, now);
    }

    public static RomFile CreateRepresented(string originalFilename, int fileId, long contentSize,
        Sha256 contentSha256, Sha1 sha1, Md5 md5, Crc32 crc32, RomRepresentationEncoding storageEncoding, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentSize);
        if (contentSha256.IsEmpty)
            throw new ArgumentException("Content identity is required.", nameof(contentSha256));
        if (!Enum.IsDefined(storageEncoding))
            throw new ArgumentOutOfRangeException(nameof(storageEncoding));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        var rom = new RomFile(0, originalFilename, fileId, contentSize, sha1, md5, crc32, now);
        rom.ContentSha256 = contentSha256;
        rom.StorageEncoding = storageEncoding;
        return rom;
    }

    /// <summary>
    ///     Rehydrates a ROM file entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static RomFile Rehydrate(
        int id,
        string originalFilename,
        int fileId,
        long size,
        Sha1 sha1,
        Md5 md5,
        Crc32 crc32,
        DateTimeOffset importedAt,
        Sha256? contentSha256 = null,
        RomRepresentationEncoding storageEncoding = RomRepresentationEncoding.Identity)
    {
        return new RomFile(
            id,
            originalFilename,
            fileId,
            size,
            sha1,
            md5,
            crc32,
            importedAt)
        { ContentSha256 = contentSha256, StorageEncoding = storageEncoding };
    }
}
