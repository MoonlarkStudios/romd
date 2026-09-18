using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class ConsumerContentArtifactResolver(
    IContentAddressableStore cas) : IConsumerContentArtifactResolver
{
    public Task<Stream?> ResolveAsync(Sha256 sha256, CancellationToken ct = default) =>
        cas.RetrieveAsync(StorageKey.FromHash(sha256), ct);
}
