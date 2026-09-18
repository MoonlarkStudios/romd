using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>
///     Represents a managed ROM file in the system.
///     Files are content-addressable based on their SHA-1 hash.
///     A RomFile only exists if it matches at least one DAT entry (strict validation).
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
    ///     The original filename when the file was first imported.
    ///     Used when exporting the file to give it a meaningful name.
    /// </summary>
    public string OriginalFilename { get; private set; }

    public int FileId { get; private set; }

    /// <summary>
    ///     File size in bytes.
    /// </summary>
    public long Size { get; private set; }

    /// <summary>
    ///     SHA-1 hash - primary identifier for CAS and DAT matching.
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
        Sha1 sha1,
        Md5 md5,
        Crc32 crc32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);

        return new RomFile(
            0,
            originalFilename,
            fileId,
            size,
            sha1,
            md5,
            crc32,
            DateTimeOffset.UtcNow);
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
        DateTimeOffset importedAt)
    {
        return new RomFile(
            id,
            originalFilename,
            fileId,
            size,
            sha1,
            md5,
            crc32,
            importedAt);
    }
}
