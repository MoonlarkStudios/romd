using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ReferenceBlobStore(IContentAddressableStore store) : IReferenceBlobStore
{
    public async Task<StoredReferenceBlob> StoreAsync(byte[] bytes, CancellationToken ct)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var result = await store.StoreAsync(input, ct: ct);
        return new(result.Size, result.CompressedSize, result.IsCompressed);
    }

    public async Task<byte[]?> ReadAsync(string hash, CancellationToken ct)
    {
        await using var stream = await store.RetrieveAsync(StorageKey.FromHash(Sha256.Parse(hash)), ct);
        if (stream is null) return null;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }
}
