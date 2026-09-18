using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Discovers truth-level source entries whose provider-neutral payload assertion can change
///     when a ROM is linked or removed. Unlike catalog matching and library impact reads, source
///     lifecycle status does not filter this topology read.
/// </summary>
public interface IRomPayloadAssertionImpactReader
{
    Task<IReadOnlyList<int>> ReadSourceEntryIdsBySha1Async(
        Sha1 sha1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> ReadSourceEntryIdsByRomFileIdAsync(
        int romFileId,
        CancellationToken cancellationToken = default);
}
