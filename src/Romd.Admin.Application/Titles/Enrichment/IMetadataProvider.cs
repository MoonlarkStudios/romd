namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Interface for metadata providers (IGDB, ScreenScraper, etc.).
///     Implementations fetch game metadata from external services.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    ///     Provider identifier (e.g., "igdb", "screenscraper").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    ///     Display name for the provider (e.g., "IGDB", "ScreenScraper").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    ///     Indicates whether the provider is configured and ready to use.
    ///     Returns false if credentials or configuration are missing.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Whether provider enablement is included in its runtime configuration.</summary>
    bool UsesRuntimeConfiguration => false;

    /// <summary>Loads a consistent configuration snapshot for this provider instance.</summary>
    Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Enriches titles by streaming contexts in and yielding results out.
    ///     Implementations may buffer/batch internally for API efficiency.
    /// </summary>
    /// <param name="contexts">Input stream of enrichment contexts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream of (context, result) pairs in processing order.</returns>
    IAsyncEnumerable<(EnrichmentContext Context, EnrichmentResult Result)> EnrichAsync(
        IAsyncEnumerable<EnrichmentContext> contexts,
        CancellationToken ct = default);
}

/// <summary>
///     Extension methods for <see cref="IMetadataProvider" />.
/// </summary>
public static class MetadataProviderExtensions
{
    /// <summary>
    ///     Enriches a single title using the streaming interface.
    /// </summary>
    public static async Task<EnrichmentResult> EnrichSingleAsync(
        this IMetadataProvider provider,
        EnrichmentContext context,
        CancellationToken ct = default)
    {
        await foreach (var (_, result) in provider.EnrichAsync(SingleItemStream(context), ct))
        {
            return result;
        }

        return EnrichmentResult.Error("Provider yielded no results");
    }

    private static async IAsyncEnumerable<EnrichmentContext> SingleItemStream(
        EnrichmentContext context)
    {
        yield return context;
        await Task.CompletedTask; // Suppress CS1998
    }
}
