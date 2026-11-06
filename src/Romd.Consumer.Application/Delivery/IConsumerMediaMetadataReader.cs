using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerMediaMetadata(Sha256 Sha256, string ContentType);

public interface IConsumerMediaMetadataReader
{
    Task<ConsumerMediaMetadata?> FindAsync(int mediaId, CancellationToken ct = default);
}
