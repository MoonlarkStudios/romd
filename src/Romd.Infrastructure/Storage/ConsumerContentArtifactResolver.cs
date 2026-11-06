using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ConsumerContentArtifactResolver(
    IContentAddressableStore cas, IRomContentReader roms) : IConsumerContentArtifactResolver
{
    public Task<Stream?> ResolveAsync(Sha256 sha256, CancellationToken ct = default) =>
        cas.RetrieveAsync(StorageKey.FromHash(sha256), ct);

    public Task<Stream?> ResolveAsync(RomDeliveryTarget target, CancellationToken ct = default) =>
        roms.OpenAsync(target, ct);

}
