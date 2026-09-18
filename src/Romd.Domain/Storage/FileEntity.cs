using Romd.Domain.Hashing;

namespace Romd.Domain.Storage;

/// <summary>
///     Represents a stored file in CAS with storage metadata.
/// </summary>
public sealed class FileEntity
{
    private FileEntity(
        int id,
        Sha256 sha256,
        long size,
        long sizeOnDisk,
        bool isCompressed,
        DateTimeOffset createdAt)
    {
        Id = id;
        Sha256 = sha256;
        Size = size;
        SizeOnDisk = sizeOnDisk;
        IsCompressed = isCompressed;
        CreatedAt = createdAt;
    }

    public int Id { get; private set; }
    public Sha256 Sha256 { get; private set; }
    public long Size { get; private set; }
    public long SizeOnDisk { get; private set; }
    public bool IsCompressed { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public double CompressionRatio => Size > 0 ? (double)SizeOnDisk / Size : 1.0;
    public long BytesSaved => IsCompressed ? Size - SizeOnDisk : 0;

    public static FileEntity CreateNew(
        Sha256 sha256,
        long size,
        long sizeOnDisk,
        bool isCompressed)
    {
        return new FileEntity(
            id: 0,
            sha256: sha256,
            size: size,
            sizeOnDisk: sizeOnDisk,
            isCompressed: isCompressed,
            createdAt: DateTimeOffset.UtcNow);
    }

    internal static FileEntity Rehydrate(
        int id,
        Sha256 sha256,
        long size,
        long sizeOnDisk,
        bool isCompressed,
        DateTimeOffset createdAt)
    {
        return new FileEntity(id, sha256, size, sizeOnDisk, isCompressed, createdAt);
    }
}
