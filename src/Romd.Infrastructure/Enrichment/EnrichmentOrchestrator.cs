using System.Runtime.CompilerServices;
using Romd.Admin.Application.MetadataProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Simplified enrichment coordinator.
///     Builds context → runs all enabled providers → stores layers → downloads media → rematerializes.
///     All providers run regardless of what previous providers found (no short-circuit).
/// </summary>
public sealed class EnrichmentOrchestrator : IEnrichmentOrchestrator
{
    private readonly IEnrichmentContextFactory _contextFactory;
    private readonly ILogger<EnrichmentOrchestrator> _logger;
    private readonly MediaDownloader _mediaDownloader;
    private readonly MetadataMerger _merger;
    private readonly EnrichmentOptions _options;
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly ITitleProviderMatchService? _matches;

    public EnrichmentOrchestrator(
        IEnumerable<IMetadataProvider> providers,
        IEnrichmentContextFactory contextFactory,
        MetadataMerger merger,
        MediaDownloader mediaDownloader,
        IOptions<EnrichmentOptions> options,
        ILogger<EnrichmentOrchestrator> logger,
        ITitleProviderMatchService? matches = null)
    {
        _providers = providers;
        _matches = matches;
        _contextFactory = contextFactory;
        _merger = merger;
        _mediaDownloader = mediaDownloader;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OrchestrationResult> EnrichTitleAsync(
        Title title,
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting enrichment for title '{TitleName}' (ID: {TitleId}) on platform '{Platform}'",
            title.Name, title.Id, platform.ShortName);

        foreach (var provider in _providers)
        {
            await provider.InitializeAsync(cancellationToken);
        }

        var enabledProviders = _providers
            .Where(p => p.IsConfigured && (p.UsesRuntimeConfiguration || _options.IsProviderEnabled(p.ProviderId)))
            .ToList();

        if (enabledProviders.Count == 0)
        {
            _logger.LogWarning("No configured providers available for enrichment");
            throw new InvalidOperationException("No enabled, configured metadata providers are available. Check Admin Settings > Metadata Providers.");
        }

        var context = await _contextFactory.CreateAsync(title, platform, cancellationToken);
        bool anyEnrichment = false;
        int errorCount = 0;
        int completedCount = 0;
        float bestConfidence = 0f;
        var artworkResults = new List<ProviderArtworkResult>();

        // Run all providers - no short-circuit
        foreach (var provider in enabledProviders)
        {
            if (_matches is not null && await _matches.IsSuppressedAsync(title.Id, provider.ProviderId, cancellationToken)) continue;
            try
            {
                // Decorate context with per-provider external ID
                var existingExt = title.GetExternalId(provider.ProviderId);
                var providerContext = context with { ExistingExternalId = existingExt?.ExternalId };

                var result = await provider.EnrichSingleAsync(providerContext, cancellationToken);
                artworkResults.Add(new(provider.ProviderId, result));

                switch (result.Outcome)
                {
                    case EnrichmentOutcome.Found:
                        if (existingExt is not null && result.ExternalId != existingExt.ExternalId &&
                            (existingExt.IsConfirmed || result.MatchConfidence < existingExt.MatchConfidence))
                        { errorCount++; break; }
                        _merger.StoreProviderResult(title, provider.ProviderId, result);

                        bool hasUsableData = result.Data?.HasAnyData() == true;
                        if (hasUsableData)
                        {
                            anyEnrichment = true;
                        }

                        // If existing external ID is confirmed, treat as perfect confidence
                        float effectiveConfidence = existingExt?.IsConfirmed == true
                            ? Math.Max(result.MatchConfidence, 1.0f)
                            : result.MatchConfidence;

                        if (effectiveConfidence > bestConfidence)
                        {
                            bestConfidence = effectiveConfidence;
                        }

                        _logger.LogDebug(
                            "Provider '{Provider}' found match for '{TitleName}' (confidence: {Confidence:P0})",
                            provider.ProviderId, title.Name, result.MatchConfidence);

                        // Download media
                        if (_options.DownloadMedia && result.MediaUrls.Count > 0)
                        {
                            await DownloadMediaAsync(title, provider.ProviderId, result.MediaUrls, cancellationToken);
                        }

                        if (!hasUsableData)
                        {
                            completedCount++;
                        }

                        break;

                    case EnrichmentOutcome.NotFound:
                        completedCount++;
                        _logger.LogDebug(
                            "Provider '{Provider}' found no match for '{TitleName}'",
                            provider.ProviderId, title.Name);
                        break;

                    case EnrichmentOutcome.PlatformNotSupported:
                        completedCount++;
                        _logger.LogDebug(
                            "Provider '{Provider}' does not support platform '{Platform}'",
                            provider.ProviderId, platform.ShortName);
                        break;

                    case EnrichmentOutcome.Error:
                        errorCount++;
                        _logger.LogWarning(
                            "Provider '{Provider}' returned error for '{TitleName}': {Error}",
                            provider.ProviderId, title.Name, result.ErrorMessage);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogWarning(ex,
                    "Error during enrichment with provider '{Provider}' for title {TitleId}",
                    provider.ProviderId, title.Id);
            }
        }

        // Rematerialize once after all providers — only if above confidence threshold
        if (anyEnrichment && bestConfidence >= _options.MinimumAutoEnrichConfidence)
        {
            await _merger.RematerializeAsync(title, cancellationToken);
        }

        if (anyEnrichment)
        {
            return OrchestrationResult.Found(bestConfidence) with { ArtworkResults = artworkResults };
        }

        if (completedCount == 0 && errorCount > 0)
        {
            return OrchestrationResult.Failed with { ArtworkResults = artworkResults };
        }

        return OrchestrationResult.NotFound with { ArtworkResults = artworkResults };
    }

    public async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
        IAsyncEnumerable<(Title Title, Platform Platform)> titles,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int windowSize = 100;

        foreach (var provider in _providers)
        {
            await provider.InitializeAsync(cancellationToken);
        }

        var enabledProviders = _providers
            .Where(p => p.IsConfigured && (p.UsesRuntimeConfiguration || _options.IsProviderEnabled(p.ProviderId)))
            .ToList();

        var enumerator = titles.GetAsyncEnumerator(cancellationToken);
        await using var _ = enumerator.ConfigureAwait(false);

        while (true)
        {
            // Buffer up to windowSize titles from the input stream
            var window = new List<(Title Title, Platform Platform)>();
            while (window.Count < windowSize && await enumerator.MoveNextAsync())
            {
                window.Add(enumerator.Current);
            }

            if (window.Count == 0)
            {
                yield break;
            }

            if (enabledProviders.Count == 0)
            {
                throw new InvalidOperationException("No enabled, configured metadata providers are available. Check Admin Settings > Metadata Providers.");
            }

            // Build contexts for this window
            var titleContexts = new Dictionary<int, (Title Title, Platform Platform, EnrichmentContext Context)>();
            foreach (var (title, platform) in window)
            {
                var context = await _contextFactory.CreateAsync(title, platform, cancellationToken);
                titleContexts[title.Id] = (title, platform, context);
            }

            // Per-title tracking
            var titleState =
                new Dictionary<int, (bool AnyEnrichment, int ErrorCount, int CompletedCount, float BestConfidence)>();
            var artworkResults = window.ToDictionary(x => x.Title.Id, _ => new List<ProviderArtworkResult>());
            foreach (var (title, _) in window)
            {
                titleState[title.Id] = (false, 0, 0, 0f);
            }

            // Run each provider with this window's titles streamed through
            foreach (var provider in enabledProviders)
            {
                var contextToTitle = new Dictionary<EnrichmentContext, int>(ReferenceEqualityComparer.Instance);
                var providerContexts = new List<EnrichmentContext>();

                foreach ((int titleId, var (title, _, baseContext)) in titleContexts)
                {
                    if (_matches is not null && await _matches.IsSuppressedAsync(titleId, provider.ProviderId, cancellationToken)) continue;
                    var existingExt = title.GetExternalId(provider.ProviderId);
                    var providerContext = baseContext with { ExistingExternalId = existingExt?.ExternalId };
                    providerContexts.Add(providerContext);
                    contextToTitle[providerContext] = titleId;
                }

                async IAsyncEnumerable<EnrichmentContext> StreamContexts()
                {
                    foreach (var ctx in providerContexts)
                    {
                        yield return ctx;
                    }

                    await Task.CompletedTask;
                }

                await foreach (var (ctx, result) in provider.EnrichAsync(StreamContexts(), cancellationToken))
                {
                    if (!contextToTitle.TryGetValue(ctx, out int titleId))
                    {
                        continue;
                    }

                    var (title, platform, _) = titleContexts[titleId];
                    artworkResults[titleId].Add(new(provider.ProviderId, result));
                    (bool anyEnrichment, int errorCount, int completedCount, float bestConfidence) =
                        titleState[titleId];
                    var existingExt = title.GetExternalId(provider.ProviderId);

                    try
                    {
                        switch (result.Outcome)
                        {
                            case EnrichmentOutcome.Found:
                                if (existingExt is not null && result.ExternalId != existingExt.ExternalId &&
                                    (existingExt.IsConfirmed || result.MatchConfidence < existingExt.MatchConfidence))
                                { errorCount++; break; }
                                _merger.StoreProviderResult(title, provider.ProviderId, result);

                                bool hasUsableData = result.Data?.HasAnyData() == true;
                                if (hasUsableData)
                                {
                                    anyEnrichment = true;
                                }

                                float effectiveConfidence = existingExt?.IsConfirmed == true
                                    ? Math.Max(result.MatchConfidence, 1.0f)
                                    : result.MatchConfidence;

                                if (effectiveConfidence > bestConfidence)
                                {
                                    bestConfidence = effectiveConfidence;
                                }

                                if (_options.DownloadMedia && result.MediaUrls.Count > 0)
                                {
                                    await DownloadMediaAsync(title, provider.ProviderId, result.MediaUrls,
                                        cancellationToken);
                                }

                                if (!hasUsableData)
                                {
                                    completedCount++;
                                }

                                break;

                            case EnrichmentOutcome.NotFound:
                                completedCount++;
                                break;

                            case EnrichmentOutcome.PlatformNotSupported:
                                completedCount++;
                                break;

                            case EnrichmentOutcome.Error:
                                errorCount++;
                                _logger.LogWarning(
                                    "Provider '{Provider}' returned error for title {TitleId}: {Error}",
                                    provider.ProviderId, titleId, result.ErrorMessage);
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _logger.LogWarning(ex,
                            "Error processing result from provider '{Provider}' for title {TitleId}",
                            provider.ProviderId, titleId);
                    }

                    titleState[titleId] = (anyEnrichment, errorCount, completedCount, bestConfidence);
                }
            }

            // Finalize each title in the window: rematerialize and yield result
            foreach (var (title, _) in window)
            {
                (bool anyEnrichment, int errorCount, int completedCount, float bestConfidence) =
                    titleState[title.Id];

                if (anyEnrichment && bestConfidence >= _options.MinimumAutoEnrichConfidence)
                {
                    await _merger.RematerializeAsync(title, cancellationToken);
                }

                if (anyEnrichment)
                {
                    yield return (title.Id, OrchestrationResult.Found(bestConfidence) with { ArtworkResults = artworkResults[title.Id] });
                }
                else if (completedCount == 0 && errorCount > 0)
                {
                    yield return (title.Id, OrchestrationResult.Failed with { ArtworkResults = artworkResults[title.Id] });
                }
                else
                {
                    yield return (title.Id, OrchestrationResult.NotFound with { ArtworkResults = artworkResults[title.Id] });
                }
            }
        }
    }

    private async Task DownloadMediaAsync(
        Title title,
        string providerId,
        IReadOnlyDictionary<MediaType, string> mediaUrls,
        CancellationToken cancellationToken)
    {
        var toDownload = mediaUrls
            // Posters and heroes use validated, role-specific acquisition after metadata is saved.
            .Where(kv => kv.Key is not (MediaType.Cover or MediaType.Background))
            .Where(kv => _options.MediaTypesToDownload.Contains(kv.Key))
            .Where(kv =>
            {
                var existing = title.GetMediaByTypeAndSource(kv.Key, providerId);
                if (existing != null)
                {
                    _logger.LogDebug(
                        "Title already has {MediaType} media from {ProviderId}, skipping download",
                        kv.Key, providerId);
                    return false;
                }

                return true;
            })
            .ToList();

        if (toDownload.Count == 0)
        {
            return;
        }

        // Download sequentially — StoreFromTempFileAsync accesses DbContext which is not thread-safe
        foreach (var kv in toDownload)
        {
            var media = await _mediaDownloader.DownloadAndStoreAsync(
                title.Id, providerId, kv.Key, kv.Value, cancellationToken);
            if (media != null)
            {
                title.AddOrReplaceMedia(media);
            }
        }
    }
}
