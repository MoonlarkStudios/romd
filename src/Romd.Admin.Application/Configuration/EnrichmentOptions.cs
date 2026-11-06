using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Configuration;

/// <summary>
///     Configuration options for the enrichment system.
/// </summary>
public sealed class EnrichmentOptions
{
    public const string SectionName = "Enrichment";

    /// <summary>
    ///     Global source priority order for metadata resolution.
    ///     Providers are tried in this order. All providers run regardless of results.
    /// </summary>
    public List<string> GlobalSourcePriority { get; set; } = ["igdb"];

    /// <summary>
    ///     Minimum match confidence to automatically apply enrichment (0.0-1.0).
    ///     Results below this threshold are stored as LowConfidence for manual review.
    /// </summary>
    public float MinimumAutoEnrichConfidence { get; set; } = 0.6f;

    /// <summary>
    ///     Whether to download and store media locally.
    /// </summary>
    public bool DownloadMedia { get; set; } = true;

    /// <summary>
    ///     Media types to download when enriching.
    /// </summary>
    public List<MediaType> MediaTypesToDownload { get; set; } =
    [
        MediaType.Cover,
        MediaType.Screenshot,
        MediaType.Background,
        MediaType.Logo
    ];

    /// <summary>
    ///     Maximum concurrent media downloads during enrichment.
    /// </summary>
    public int MaxMediaDownloadConcurrency { get; set; } = 8;

    /// <summary>
    ///     Number of titles to persist in each batch during bulk enrichment.
    /// </summary>
    public int PersistenceBatchSize { get; set; } = 25;

    /// <summary>
    ///     Region priority for representative game selection.
    ///     Higher priority regions are preferred when multiple versions exist.
    /// </summary>
    public List<string> RegionPriority { get; set; } = ["USA", "WORLD", "EUROPE", "JAPAN"];

    /// <summary>
    ///     Per-provider configuration settings.
    /// </summary>
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["igdb"] = new ProviderSettings { RateLimit = 4, Enabled = true }
    };

    /// <summary>
    ///     Gets the rate limit for a specific provider (requests per second).
    /// </summary>
    public int GetProviderRateLimit(string providerId)
    {
        return Providers.TryGetValue(providerId, out var settings)
            ? settings.RateLimit
            : 4;
    }

    /// <summary>
    ///     Gets the global source priority as a read-only list.
    /// </summary>
    public IReadOnlyList<string> GetSourcePriorityOrder() => GlobalSourcePriority;

    /// <summary>
    ///     Checks if a provider is enabled.
    /// </summary>
    public bool IsProviderEnabled(string providerId)
    {
        return !Providers.TryGetValue(providerId, out var settings) || settings.Enabled;
    }
}

/// <summary>
///     Per-provider configuration settings.
/// </summary>
public sealed class ProviderSettings
{
    /// <summary>
    ///     Whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Maximum requests per second for this provider.
    /// </summary>
    public int RateLimit { get; set; } = 4;

    // TODO: Per-provider media type filtering is not yet implemented — orchestrator uses global MediaTypesToDownload only
    /// <summary>
    ///     Media types this provider should download.
    ///     Null means use the global MediaTypesToDownload.
    /// </summary>
    public List<MediaType>? MediaTypesToDownload { get; set; }
}
