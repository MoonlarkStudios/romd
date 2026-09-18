using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Reads source-neutral evidence used to build metadata enrichment contexts for a title.
/// </summary>
public interface ITitleEnrichmentEvidenceReader
{
    /// <summary>
    ///     Returns evidence in stable source order. Consumers use that order as the final tiebreaker
    ///     when candidate scores and names are equal; source identity remains private to the adapter.
    /// </summary>
    Task<IReadOnlyList<TitleEnrichmentEvidence>> ReadAsync(
        int titleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Metadata matching evidence for one title candidate. Deliberately excludes source identity.
/// </summary>
public sealed record TitleEnrichmentEvidence(
    string Name,
    string? Year,
    string? Manufacturer,
    string? Region,
    string? Revision,
    string? DevelopmentStatus,
    bool HasDefinedRoms,
    IReadOnlyList<OwnedRomHashEvidence> OwnedRomHashes)
{
    public bool HasOwnedRom => OwnedRomHashes.Count > 0;
}

/// <summary>
///     Correlated hashes and size for one owned ROM definition.
/// </summary>
public sealed record OwnedRomHashEvidence(
    Sha1? Sha1,
    Md5? Md5,
    Crc32? Crc32,
    long Size);
