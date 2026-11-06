using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Runs every enabled, configured metadata provider for a title against its trusted provider identity,
///     stores accepted results as provider layers, stages provider media, and rematerializes once.
///     Providers never short-circuit each other. The orchestrator mutates only the in-memory title and
///     performs no database transaction: callers publish the title, staged media and follow-up work
///     atomically under their own revision and fence semantics, and own the returned staged media.
/// </summary>
public sealed class EnrichmentOrchestrator : IEnrichmentOrchestrator
{
    /// <summary>Titles buffered per stream window; bounds memory while keeping provider-major ordering.</summary>
    public const int StreamWindowSize = 100;

    private const string NoProvidersMessage =
        "No enabled, configured metadata providers are available. Check Admin Settings > Metadata Providers.";

    private readonly TimeProvider _clock;
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IEnrichmentContextFactory _contexts;
    private readonly IProviderIdentityService _identities;
    private readonly ITitleProviderMatchService _matches;
    private readonly IMetadataRematerializer _rematerializer;
    private readonly IEnrichmentMediaStager _mediaStager;
    private readonly IArtworkCurationRepository _curation;
    private readonly EnrichmentOptions _options;
    private readonly ILogger<EnrichmentOrchestrator> _logger;

    public EnrichmentOrchestrator(
        IEnumerable<IMetadataProvider> providers,
        IEnrichmentContextFactory contexts,
        IProviderIdentityService identities,
        ITitleProviderMatchService matches,
        IMetadataRematerializer rematerializer,
        IEnrichmentMediaStager mediaStager,
        IArtworkCurationRepository curation,
        IOptions<EnrichmentOptions> options,
        ILogger<EnrichmentOrchestrator> logger,
        TimeProvider clock)
    {
        _providers = providers;
        _contexts = contexts;
        _identities = identities;
        _matches = matches;
        _rematerializer = rematerializer;
        _mediaStager = mediaStager;
        _curation = curation;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
    }

    public async Task<OrchestrationResult> EnrichTitleAsync(
        Title title,
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting enrichment for title '{TitleName}' (ID: {TitleId}) on platform '{Platform}'",
            title.Name, title.Id, platform.ShortName);

        var enabledProviders = await GetEnabledProvidersAsync(cancellationToken);
        if (enabledProviders.Count == 0)
        {
            _logger.LogWarning("No configured providers available for enrichment");
            throw new InvalidOperationException(NoProvidersMessage);
        }

        var context = await _contexts.CreateAsync(title, platform, cancellationToken);
        await using var stagedMedia = new StagedMediaLedger([title.Id], _logger);
        var tally = new TitleEnrichmentTally();
        var artworkResults = new List<ProviderArtworkResult>();

        bool identityDiscovered = false;
        foreach (var provider in enabledProviders)
        {
            // A suppressed link opts the title out of this provider entirely; it is not an outcome.
            if (await _matches.IsSuppressedAsync(title.Id, provider.ProviderId, cancellationToken))
                continue;
            try
            {
                // Identity is resolved before fetching: only a trusted provider game may be fetched.
                if (!identityDiscovered)
                {
                    await _identities.DiscoverAsync(title.Id, null, cancellationToken);
                    identityDiscovered = true;
                }
                var trustedId = await _identities.GetTrustedAsync(title.Id, provider.ProviderId, cancellationToken);
                if (trustedId is null)
                {
                    tally.RecordCompleted();
                    continue;
                }

                var result = await provider.EnrichSingleAsync(context with { ExistingExternalId = trustedId },
                    cancellationToken);
                artworkResults.Add(new(provider.ProviderId, result));
                await ApplyResultAsync(title, platform, provider.ProviderId, trustedId, result, tally,
                    stagedMedia.For(title.Id), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tally.RecordError();
                _logger.LogWarning(ex,
                    "Error during enrichment with provider '{Provider}' for title {TitleId}",
                    provider.ProviderId, title.Id);
            }
        }

        return await CompleteAsync(title, tally, artworkResults, stagedMedia, cancellationToken);
    }

    public async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
        IAsyncEnumerable<(Title Title, Platform Platform)> titles,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enabledProviders = await GetEnabledProvidersAsync(cancellationToken);

        var enumerator = titles.GetAsyncEnumerator(cancellationToken);
        await using var _ = enumerator.ConfigureAwait(false);

        while (true)
        {
            var window = new List<(Title Title, Platform Platform)>();
            while (window.Count < StreamWindowSize && await enumerator.MoveNextAsync())
            {
                window.Add(enumerator.Current);
            }

            if (window.Count == 0)
            {
                yield break;
            }

            if (enabledProviders.Count == 0)
            {
                throw new InvalidOperationException(NoProvidersMessage);
            }

            // Media for titles not yet handed to the consumer is released if enumeration stops early.
            await using var stagedMedia = new StagedMediaLedger(window.Select(x => x.Title.Id), _logger);

            var titleContexts = new Dictionary<int, (Title Title, Platform Platform, EnrichmentContext Context)>();
            foreach (var (title, platform) in window)
            {
                var context = await _contexts.CreateAsync(title, platform, cancellationToken);
                titleContexts[title.Id] = (title, platform, context);
            }

            var tallies = window.ToDictionary(x => x.Title.Id, _ => new TitleEnrichmentTally());
            var artworkResults = window.ToDictionary(x => x.Title.Id, _ => new List<ProviderArtworkResult>());

            // Discovery is title-scoped while provider fetching remains provider-major. Trust is
            // still re-read for each fetch and again before applying external results.
            var discoveredIdentities = new HashSet<int>();
            // Provider-major: each provider streams the whole window before the next provider starts.
            foreach (var provider in enabledProviders)
            {
                var contextToTitle = new Dictionary<EnrichmentContext, int>(ReferenceEqualityComparer.Instance);
                var providerContexts = new List<EnrichmentContext>();

                foreach (var (titleId, (_, _, baseContext)) in titleContexts)
                {
                    if (await _matches.IsSuppressedAsync(titleId, provider.ProviderId, cancellationToken))
                        continue;
                    if (discoveredIdentities.Add(titleId))
                        await _identities.DiscoverAsync(titleId, null, cancellationToken);
                    var trustedId = await _identities.GetTrustedAsync(titleId, provider.ProviderId, cancellationToken);
                    if (trustedId is null)
                        continue;
                    var providerContext = baseContext with { ExistingExternalId = trustedId };
                    providerContexts.Add(providerContext);
                    contextToTitle[providerContext] = titleId;
                }

                await foreach (var (ctx, result) in provider.EnrichAsync(
                                   ToAsyncEnumerable(providerContexts), cancellationToken))
                {
                    if (!contextToTitle.TryGetValue(ctx, out int titleId))
                    {
                        continue;
                    }

                    var (title, platform, _) = titleContexts[titleId];
                    artworkResults[titleId].Add(new(provider.ProviderId, result));
                    var tally = tallies[titleId];
                    try
                    {
                        await ApplyResultAsync(title, platform, provider.ProviderId, ctx.ExistingExternalId!, result,
                            tally, stagedMedia.For(titleId), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        tally.RecordError();
                        _logger.LogWarning(ex,
                            "Error processing result from provider '{Provider}' for title {TitleId}",
                            provider.ProviderId, titleId);
                    }
                }
            }

            foreach (var (title, _) in window)
            {
                yield return (title.Id, await CompleteAsync(title, tallies[title.Id], artworkResults[title.Id],
                    stagedMedia, cancellationToken));
            }
        }
    }

    private async Task<IReadOnlyList<IMetadataProvider>> GetEnabledProvidersAsync(CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            await provider.InitializeAsync(ct);
        }

        return _providers
            .Where(p => p.IsConfigured && (p.UsesRuntimeConfiguration || _options.IsProviderEnabled(p.ProviderId)))
            .ToList();
    }

    private async Task ApplyResultAsync(
        Title title,
        Platform platform,
        string providerId,
        string trustedId,
        EnrichmentResult result,
        TitleEnrichmentTally tally,
        ICollection<StagedTitleMedia> stagedMedia,
        CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case EnrichmentOutcome.Found:
                if (!await IsAcceptedAsync(title, providerId, trustedId, result, ct))
                {
                    tally.RecordError();
                    return;
                }

                ProviderMetadataLayers.Store(title, providerId, result, _clock.GetUtcNow());
                bool hasUsableData = result.Data?.HasAnyData() == true;
                // Shared identity acceptance already authorizes the result; the provider's own score is advisory.
                tally.RecordAccepted(Math.Max(result.MatchConfidence, 1.0f), hasUsableData);
                _logger.LogDebug(
                    "Provider '{Provider}' found match for '{TitleName}' (confidence: {Confidence:P0})",
                    providerId, title.Name, result.MatchConfidence);

                if (_options.DownloadMedia && result.MediaUrls.Count > 0)
                {
                    await StageMediaAsync(title, providerId, result.MediaUrls, stagedMedia, ct);
                }

                if (!hasUsableData)
                {
                    tally.RecordCompleted();
                }

                return;

            case EnrichmentOutcome.NotFound:
                tally.RecordCompleted();
                _logger.LogDebug("Provider '{Provider}' found no match for '{TitleName}'", providerId, title.Name);
                return;

            case EnrichmentOutcome.PlatformNotSupported:
                tally.RecordCompleted();
                _logger.LogDebug("Provider '{Provider}' does not support platform '{Platform}'",
                    providerId, platform.ShortName);
                return;

            case EnrichmentOutcome.Error:
                tally.RecordError();
                _logger.LogWarning("Provider '{Provider}' returned error for title {TitleId}: {Error}",
                    providerId, title.Id, result.ErrorMessage);
                return;
        }
    }

    /// <summary>
    ///     A found result is applied only when it is the game that was trusted at fetch time, is still trusted
    ///     now, and does not displace a confirmed or more confident association already on the title.
    /// </summary>
    private async Task<bool> IsAcceptedAsync(
        Title title, string providerId, string trustedId, EnrichmentResult result, CancellationToken ct)
    {
        if (result.ExternalId != trustedId ||
            await _identities.GetTrustedAsync(title.Id, providerId, ct) != result.ExternalId)
        {
            return false;
        }

        var existing = title.GetExternalId(providerId);
        return existing is null || result.ExternalId == existing.ExternalId ||
               (!existing.IsConfirmed && result.MatchConfidence >= existing.MatchConfidence);
    }

    private async Task StageMediaAsync(
        Title title,
        string providerId,
        IReadOnlyDictionary<MediaType, string> mediaUrls,
        ICollection<StagedTitleMedia> stagedMedia,
        CancellationToken ct)
    {
        var wanted = mediaUrls
            // Posters and heroes use validated, role-specific acquisition after metadata is saved.
            .Where(kv => kv.Key is not (MediaType.Cover or MediaType.Background))
            .Where(kv => _options.MediaTypesToDownload.Contains(kv.Key))
            .Where(kv =>
            {
                if (title.GetMediaByTypeAndSource(kv.Key, providerId) is null)
                    return true;
                _logger.LogDebug("Title already has {MediaType} media from {ProviderId}, skipping download",
                    kv.Key, providerId);
                return false;
            })
            .ToList();

        if (wanted.Count == 0)
        {
            return;
        }

        // Curators may reject a provider asset; a rejected URL is never acquired again.
        var excluded = (await _curation.GetExcludedUrlsAsync(title.Id, ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (mediaType, url) in wanted)
        {
            if (excluded.Contains(url))
                continue;
            var media = await _mediaStager.StageAsync(title.Id, providerId, mediaType, url, ct);
            if (media is not null)
            {
                stagedMedia.Add(media);
            }
        }
    }

    private async Task<OrchestrationResult> CompleteAsync(
        Title title,
        TitleEnrichmentTally tally,
        IReadOnlyList<ProviderArtworkResult> artworkResults,
        StagedMediaLedger stagedMedia,
        CancellationToken ct)
    {
        if (tally.ShouldRematerialize(_options.MinimumAutoEnrichConfidence))
        {
            await _rematerializer.RematerializeAsync(title, ct);
        }

        return tally.ToResult() with { ArtworkResults = artworkResults, StagedMedia = stagedMedia.HandOff(title.Id) };
    }

    private static async IAsyncEnumerable<EnrichmentContext> ToAsyncEnumerable(IEnumerable<EnrichmentContext> contexts)
    {
        foreach (var context in contexts)
        {
            yield return context;
        }

        await Task.CompletedTask;
    }
}
