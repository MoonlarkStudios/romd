using Romd.Application.Common.Artwork;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ArtworkDeliveryService(IArtworkReader reader, IContentAddressableStore store) : IArtworkDelivery
{
    public async Task<ArtworkDelivery?> OpenAsync(int assetId, string variantName, string contentVersion,
        CancellationToken ct = default)
    {
        var file = await reader.FindVariantAsync(assetId, variantName, contentVersion, ct);
        if (file is null) return null;
        var content = await store.RetrieveAsync(StorageKey.FromHash(file.Sha256), ct);
        return content is null ? null : new ArtworkDelivery(content, file.ContentType, file.Sha256.ToString());
    }
}
