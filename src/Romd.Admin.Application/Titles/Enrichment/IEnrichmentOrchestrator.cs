using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Orchestrates enrichment across multiple metadata providers.
///     Runs all enabled providers, merges results, and rematerializes once.
/// </summary>
public interface IEnrichmentOrchestrator
{
    /// <summary>
    ///     Enriches a title using configured providers.
    /// </summary>
    /// <param name="title">The title to enrich (loaded with ExternalIds and Media).</param>
    /// <param name="platform">The platform for provider priority lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating whether enrichment was found and the best match confidence.</returns>
    Task<OrchestrationResult> EnrichTitleAsync(
        Title title,
        Platform platform,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enriches a stream of titles using windowed processing (100 titles per window).
    ///     Within each window, all providers run across all titles before results are yielded.
    ///     This bounds memory to O(window_size) while preserving provider-major ordering.
    /// </summary>
    /// <param name="titles">Stream of titles and their platform to enrich.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of (titleId, result) pairs as each title completes.</returns>
    IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
        IAsyncEnumerable<(Title Title, Platform Platform)> titles,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Result from orchestrating enrichment across providers.
/// </summary>
public sealed record OrchestrationResult(bool Enriched, float BestConfidence, bool AllErrored)
{
    public IReadOnlyList<ProviderArtworkResult> ArtworkResults { get; init; } = [];
    public static readonly OrchestrationResult NotFound = new(false, 0f, false);
    public static readonly OrchestrationResult Failed = new(false, 0f, true);

    public static OrchestrationResult Found(float bestConfidence) => new(true, bestConfidence, false);
}

public sealed record ProviderArtworkResult(string ProviderId, EnrichmentResult Result);
