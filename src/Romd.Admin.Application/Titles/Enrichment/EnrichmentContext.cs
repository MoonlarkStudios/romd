using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Context passed to a metadata provider for enrichment.
///     Contains all information the provider needs to find and return metadata.
/// </summary>
public sealed record EnrichmentContext
{
    /// <summary>
    ///     Display name of the title.
    /// </summary>
    public required string TitleName { get; init; }

    /// <summary>
    ///     Canonical platform short name (e.g., "snes", "genesis").
    ///     Providers translate this to their own platform IDs internally.
    /// </summary>
    public required string PlatformShortName { get; init; }

    /// <summary>
    ///     Existing external ID for this provider, if any.
    ///     Allows re-fetching by known ID instead of searching.
    /// </summary>
    public string? ExistingExternalId { get; init; }

    /// <summary>
    ///     Year from the representative DAT game entry (e.g., "1994").
    /// </summary>
    public string? Year { get; init; }

    /// <summary>
    ///     Manufacturer from the representative DAT game entry.
    /// </summary>
    public string? Manufacturer { get; init; }

    /// <summary>
    ///     Region from the representative DAT game entry (e.g., "USA", "Europe").
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    ///     Per-ROM hash sets from owned ROM files.
    ///     Preserves correlation between hash types for a single ROM
    ///     (e.g., SHA1+MD5+CRC32+Size for the same file).
    /// </summary>
    public IReadOnlyList<RomHashSet> KnownHashes { get; init; } = [];
}

/// <summary>
///     Correlated hash set for a single ROM file.
///     Providers can try SHA1 first, then fall back to MD5+Size or CRC32+Size
///     for the same physical ROM.
/// </summary>
public sealed record RomHashSet
{
    public Sha1? Sha1 { get; init; }
    public Md5? Md5 { get; init; }
    public Crc32? Crc32 { get; init; }
    public long Size { get; init; }
}
