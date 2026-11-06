using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>Reads reference assets. The consumer host uses it alone: it never publishes files.</summary>
public sealed class ReadOnlyReferenceBlobStore(IContentAddressableStore store) : IReferenceBlobStore
{
    public Task<IStagedReferencePublication> PublishStagedAsync(IReadOnlyList<byte[]> contents, CancellationToken ct) =>
        throw new InvalidOperationException("This host reads reference assets but does not publish them.");

    public async Task<byte[]?> ReadAsync(string hash, CancellationToken ct)
    {
        await using var stream = await store.RetrieveAsync(StorageKey.FromHash(Sha256.Parse(hash)), ct);
        if (stream is null)
            return null;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }
}
