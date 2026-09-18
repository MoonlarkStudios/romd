using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

public interface IConsumerContentArtifactResolver
{
    Task<Stream?> ResolveAsync(Sha256 sha256, CancellationToken ct = default);
}
