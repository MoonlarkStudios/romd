namespace Romd.Application.Common.ReferenceCatalog;

public sealed record StoredReferenceBlob(long Size, long SizeOnDisk, bool IsCompressed);

/// <summary>Blob IO only. The caller owns the database transaction and per-hash mutation lock.</summary>
public interface IReferenceBlobStore
{
    Task<StoredReferenceBlob> StoreAsync(byte[] bytes, CancellationToken ct);
    Task<byte[]?> ReadAsync(string hash, CancellationToken ct);
}
