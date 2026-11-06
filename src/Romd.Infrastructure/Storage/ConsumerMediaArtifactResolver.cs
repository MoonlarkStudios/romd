using Romd.Consumer.Application.Delivery;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ConsumerMediaArtifactResolver(
    IConsumerMediaMetadataReader metadata,
    IContentAddressableStore cas) : IConsumerMediaArtifactResolver
{
    public async Task<ConsumerMediaArtifact?> ResolveMediaAsync(
        ConsumerMediaArtifactRequest request,
        CancellationToken ct = default)
    {
        var media = await metadata.FindAsync(request.MediaId, ct);

        if (media is null)
        {
            return null;
        }

        var stream = await cas.RetrieveAsync(StorageKey.FromHash(media.Sha256), ct);

        return stream is null
            ? null
            : new ConsumerMediaArtifact(stream, media.ContentType);
    }
}
