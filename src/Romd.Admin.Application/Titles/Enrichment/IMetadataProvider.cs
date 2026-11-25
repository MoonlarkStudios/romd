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
    ///     Fetches metadata for explicit game IDs already resolved by the application.
    ///     Implementations may buffer/batch internally for API efficiency.
    /// </summary>
    /// <param name="requests">Input stream of game lookup requests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Each original request paired with its result, in request order.</returns>
    IAsyncEnumerable<(MetadataFetchRequest Request, EnrichmentResult Result)> FetchAsync(
        IAsyncEnumerable<MetadataFetchRequest> requests,
        CancellationToken ct = default);
}

/// <summary>A provider game ID chosen by the application, with its enrichment context.</summary>
public sealed record MetadataFetchRequest(string GameId, EnrichmentContext Context);

public static class MetadataProviderExtensions
{
    /// <summary>
    ///     Enriches a single title using the streaming interface.
    /// </summary>
    public static async Task<EnrichmentResult> FetchSingleAsync(
        this IMetadataProvider provider,
        MetadataFetchRequest request,
        CancellationToken ct = default)
    {
        await foreach (var (_, result) in provider.FetchAsync(SingleItemStream(request), ct))
        {
            return result;
        }

        return EnrichmentResult.Error("Provider yielded no results");
    }

    private static async IAsyncEnumerable<MetadataFetchRequest> SingleItemStream(MetadataFetchRequest request)
    {
        yield return request;
        await Task.CompletedTask; // Suppress CS1998
    }
}
