using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Result from a metadata provider's enrichment attempt.
///     Replaces both MetadataSearchResult and MetadataResult.
/// </summary>
public sealed record EnrichmentResult
{
    public IReadOnlyList<Romd.Admin.Application.Artwork.EnrichmentArtworkCandidate> Artwork { get; init; } = [];
    /// <summary>
    ///     The outcome of the enrichment attempt.
    /// </summary>
    public required EnrichmentOutcome Outcome { get; init; }

    /// <summary>
    ///     The external system's ID for this game (e.g., IGDB game ID).
    ///     Null when Outcome is NotFound or Error.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    ///     Match confidence score from 0.0 to 1.0.
    ///     Only meaningful when Outcome is Found.
    /// </summary>
    public float MatchConfidence { get; init; }

    /// <summary>
    ///     Enrichment data from the provider.
    ///     Null when Outcome is NotFound or Error.
    /// </summary>
    public EnrichmentData? Data { get; init; }

    /// <summary>
    ///     Available media URLs keyed by MediaType.
    ///     URLs are provider-specific and may require downloading.
    /// </summary>
    public IReadOnlyDictionary<MediaType, string> MediaUrls { get; init; } =
        new Dictionary<MediaType, string>();

    /// <summary>
    ///     Error message when Outcome is Error.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Creates a successful result with data.
    /// </summary>
    public static EnrichmentResult Found(
        string externalId,
        float matchConfidence,
        EnrichmentData data,
        IReadOnlyDictionary<MediaType, string>? mediaUrls = null) => new()
    {
        Outcome = EnrichmentOutcome.Found,
        ExternalId = externalId,
        MatchConfidence = matchConfidence,
        Data = data,
        MediaUrls = mediaUrls ?? new Dictionary<MediaType, string>()
    };

    /// <summary>
    ///     Creates a not-found result.
    /// </summary>
    public static EnrichmentResult NotFound() => new()
    {
        Outcome = EnrichmentOutcome.NotFound
    };

    /// <summary>
    ///     Creates an error result.
    /// </summary>
    public static EnrichmentResult Error(string message) => new()
    {
        Outcome = EnrichmentOutcome.Error,
        ErrorMessage = message
    };

    /// <summary>
    ///     Creates a result indicating the platform is not supported by this provider.
    /// </summary>
    public static EnrichmentResult PlatformNotSupported() => new()
    {
        Outcome = EnrichmentOutcome.PlatformNotSupported
    };
}

/// <summary>
///     Outcome of an enrichment attempt.
/// </summary>
public enum EnrichmentOutcome
{
    /// <summary>
    ///     A match was found and data is available.
    /// </summary>
    Found,

    /// <summary>
    ///     No match was found in this provider.
    /// </summary>
    NotFound,

    /// <summary>
    ///     The platform is not supported by this provider.
    /// </summary>
    PlatformNotSupported,

    /// <summary>
    ///     An error occurred during the enrichment attempt.
    /// </summary>
    Error
}
