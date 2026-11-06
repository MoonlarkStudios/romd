using Romd.Application.Common.Content;
using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

public interface IConsumerContentArtifactResolver
{
    /// <summary>Concrete CAS lane, including BIOS; never reconstructs a logical ROM.</summary>
    Task<Stream?> ResolveAsync(Sha256 sha256, CancellationToken ct = default);
    Task<Stream?> ResolveAsync(RomDeliveryTarget target, CancellationToken ct = default);
}
