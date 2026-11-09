using ErrorOr;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Artwork;

/// <summary>Acquires missing roles in policy order using trusted provider identities.</summary>
public sealed class LinkedProviderArtworkService(
    IEnumerable<IArtworkProviderBrowser> browsers, IArtworkAssetSource source, ITitleProviderMatchService matches,
    IArtworkAcquisitionStore acquisition, IArtworkCurationRepository curation, IArtworkImageProcessor processor,
    IFileStorageService files, IUnitOfWork unitOfWork, IOptions<EnrichmentOptions> options,
    IMemoryCache? candidateCache = null, ArtworkCandidateFailureCache? failureCache = null, TimeProvider? timeProvider = null) : IAutomaticArtworkService
{
    private const int MaxPagesPerProvider = 3;
    private const int MaxDownloadsPerRole = 16;
    private const int MaxDownloadsPerProvider = 8;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
        IReadOnlyList<ProviderArtworkResult>? results = null)
    {
        Guid? titleRevision = null;
        IReadOnlyList<TitleProviderMatch>? links = null;
        ProviderIdentityDiscoveryResult? discovery = null;
        var settings = await acquisition.GetSettingsAsync(ct);
        foreach (var role in Enum.GetValues<ArtworkRole>())
        {
            ct.ThrowIfCancellationRequested();
            var states = await curation.GetStatesAsync(title.Id, ct);
            var gallery = await curation.GetGalleryAsync(title.Id, ct);
            if (states.IsError || gallery.IsError)
                return;
            var selection = states.Value.Single(x => x.Role == role);
            if (selection.Mode == ArtworkSelectionMode.Empty)
            { await Record("LeftEmpty", null); continue; }
            if (selection.Mode == ArtworkSelectionMode.Pinned || selection.PendingJobId is not null ||
                gallery.Value.Any(x => x.Role == role && x.IsEligible) || role == ArtworkRole.Poster && title.Media.Any(x => x.Type == MediaType.Cover))
            { await Record("AlreadyPresent", null); continue; }
            var enabled = role switch
            {
                ArtworkRole.Poster => settings.FillPosters,
                ArtworkRole.Hero => settings.FillHeroes,
                ArtworkRole.Logo => settings.FillLogos,
                ArtworkRole.Backdrop => settings.FillBackdrops,
                _ => false
            };
            var mediaType = role switch
            {
                ArtworkRole.Poster => MediaType.Cover,
                ArtworkRole.Logo => MediaType.Logo,
                _ => MediaType.Background
            };
            if (!enabled || !options.Value.DownloadMedia || !options.Value.MediaTypesToDownload.Contains(mediaType))
            { await Record("Disabled", null); continue; }

            if (links is null)
            {
                discovery = await matches.DiscoverAsync(title.Id, context, ct);
                var discovered = await matches.GetAsync(title.Id, ct);
                if (discovered.IsError)
                    return;
                links = discovered.Value;
                titleRevision = await acquisition.GetTitleRevisionAsync(title.Id, ct);
            }
            var outcome = "ProviderDisabled";
            DateTimeOffset? retryAt = null;
            var downloadsRemaining = MaxDownloadsPerRole;
            string? outcomeProvider = null;
            var order = ArtworkAcquisitionPolicy.ProviderOrder(role, await acquisition.GetProviderOrderAsync(role, ct));
            foreach (var providerId in order)
            {
                var browser = browsers.SingleOrDefault(x => x.Capabilities.ProviderId == providerId &&
                    x.Capabilities.Roles.Any(capability => capability.Role == role));
                if (browser is null)
                    continue;
                if (discovery?.RetryAfterByProvider.TryGetValue(providerId, out var identityRetryAt) == true)
                {
                    retryAt = retryAt is { } prior && prior < identityRetryAt ? prior : identityRetryAt;
                    continue;
                }
                var link = links.SingleOrDefault(x => x.ProviderId == providerId);
                if (link is null)
                    continue;
                if (!link.IsAvailable)
                { Remember("ProviderUnconfigured"); continue; }
                if (link.State == "Unlinked")
                { Remember("Unlinked"); continue; }
                if (link.State == "Unavailable")
                { Remember("Failed"); continue; }
                if (link.ExternalId is null || link.State is not ("Confirmed" or "AutoMatched"))
                { Remember("NeedsMatch"); continue; }
                if (downloadsRemaining <= 0)
                    break;
                var providerThrottled = false;
                var providerDownloadsRemaining = Math.Min(MaxDownloadsPerProvider, downloadsRemaining);
                try
                {
                    var emptyCacheKey = ("automatic-artwork-empty", title.Id, providerId, link.ExternalId,
                        role, link.Revision, selection.Revision, settings.Revision);
                    if (candidateCache?.TryGetValue(emptyCacheKey, out _) == true)
                    { Remember("Unavailable"); continue; }
                    var rejected = await acquisition.GetRejectedCandidatesAsync(title.Id, role, providerId, link.ExternalId, ct);
                    var excluded = await curation.GetExcludedUrlsAsync(title.Id, ct);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var sawEligible = false;
                    var searchFailed = false;
                    var page = 0;
                    // Acquire progressively: a usable first-page image needs no further enumeration.
                    // Provider order is retained within each role-specific preference tier.
                    for (var count = 0; count < MaxPagesPerProvider && providerDownloadsRemaining > 0; count++)
                    {
                        var found = await ReadCandidatesAsync(browser, link.ExternalId, role, page, ct);
                        if (found.IsError)
                        {
                            searchFailed = true;
                            if (!RememberThrottle(found.Errors))
                                Remember("Failed");
                            break;
                        }
                        var ranked = found.Value.Items
                            .Where(x => x.ProviderGameId == link.ExternalId && x.Role == role && seen.Add(x.ProviderAssetId))
                            .Where(x => !excluded.Contains(x.AssetUrl.AbsoluteUri) && !rejected.Contains(x.ProviderAssetId))
                            .Where(x => ArtworkCandidateRanking.IsSuitable(role, x.Width, x.Height))
                            .OrderBy(x => role == ArtworkRole.Backdrop
                                ? ArtworkCandidateRanking.BackdropPreference(x.Width, x.Height)
                                : ArtworkCandidateRanking.Preference(role, x.Style));
                        foreach (var candidate in ranked)
                        {
                            sawEligible = true;
                            if (role == ArtworkRole.Backdrop && settings.ReviewBackdrops)
                            { Remember("NeedsReview"); break; }
                            var key = new ArtworkCandidateFailureCache.ProviderKey(link.ExternalId, role, candidate.ProviderAssetId, candidate.AssetUrl.AbsoluteUri);
                            if (failureCache?.Contains(providerId, key) == true)
                            { Remember("Failed"); continue; }
                            if (providerDownloadsRemaining <= 0)
                                break;
                            providerDownloadsRemaining--;
                            downloadsRemaining--;
                            var downloaded = await RetryAsync(() => source.DownloadAsync(providerId, link.ExternalId, candidate.ProviderAssetId,
                                role, candidate.AssetUrl.AbsoluteUri, ct), ct);
                            if (downloaded.IsError)
                            {
                                if (RememberThrottle(downloaded.Errors))
                                    break;
                                failureCache?.Record(providerId, key);
                                Remember("Failed");
                                continue;
                            }
                            var image = await processor.ProcessAsync(downloaded.Value.Bytes, role, ct);
                            if (image.IsError || !ArtworkCandidateRanking.IsSuitable(role, image.Value.Width, image.Value.Height) ||
                                role == ArtworkRole.Logo && image.Value.HasUsableTransparency == false)
                            {
                                failureCache?.Record(providerId, key);
                                Remember("Failed");
                                continue;
                            }
                            var superseded = false;
                            await using var staged = await PreparedArtwork.StageAsync(files, downloaded.Value.Bytes, image.Value, ct);
                            await context.ExecuteOwnedMutationAsync(async token =>
                            {
                                if (!await acquisition.LockMissingAsync(title.Id, role, selection.Revision, token) ||
                                    await matches.GetLinkRevisionAsync(title.Id, providerId, link.ExternalId, token) != link.Revision ||
                                    await acquisition.GetTitleRevisionAsync(title.Id, token) != titleRevision)
                                { superseded = true; return; }
                                var publication = await files.PublishStagedAsync(staged.Files, token);
                                if (publication.RequiresFlush)
                                    await unitOfWork.FlushAsync(token);
                                var content = staged.Retain(publication.ReadAfterFlush(), candidate.Attribution, candidate.SourcePageUrl);
                                await curation.StageLocalAssetAsync(title.Id, role, providerId, content, token,
                                    link.ExternalId, candidate.ProviderAssetId);
                                await acquisition.RecordAsync(title.Id, role, "Updated", providerId, token, context.JobId);
                            }, ct);
                            outcome = superseded ? "Superseded" : "Updated";
                            outcomeProvider = providerId;
                            break;
                        }
                        if (outcome is "Updated" or "Superseded" or "NeedsReview" || providerThrottled)
                            break;
                        if (found.Value.NextPage is not { } next || next <= page)
                            break;
                        page = next;
                    }
                    if (!sawEligible && !searchFailed)
                        candidateCache?.Set(emptyCacheKey, true, TimeSpan.FromMinutes(10));
                    if (outcome == "ProviderDisabled")
                        Remember("Unavailable");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException ||
                    ex is OperationCanceledException && !ct.IsCancellationRequested)
                { Remember("Failed"); }
                if (outcome is "Updated" or "Superseded")
                    break;

                bool RememberThrottle(IReadOnlyList<Error> errors)
                {
                    if (ArtworkProviderRetryPolicy.GetDeferredUntil(errors, _clock.GetUtcNow()) is not { } due)
                        return false;
                    providerThrottled = true;
                    retryAt = retryAt is { } prior && prior < due ? prior : due;
                    return true;
                }

                void Remember(string status)
                {
                    if (Priority(status) <= Priority(outcome))
                        return;
                    outcome = status;
                    outcomeProvider = providerId;
                }
            }
            if (retryAt is { } due && outcome is not ("Updated" or "Superseded" or "NeedsReview"))
            {
                context.RequestDeferral(due);
                return;
            }
            if (outcome != "Updated")
                await Record(outcome, outcomeProvider);
            Task Record(string status, string? provider) => context.ExecuteOwnedMutationAsync(
                token => acquisition.RecordAsync(title.Id, role, status, provider, token, context.JobId), ct);
        }
    }

    private static async Task<ErrorOr<ProviderArtworkPage>> ReadCandidatesAsync(
        IArtworkProviderBrowser browser, string gameId, ArtworkRole role, int page, CancellationToken ct)
    {
        // Backdrops compare the bounded page set before accepting a crop fallback.
        // Other roles retain progressive acquisition and stop at the first usable image.
        if (role != ArtworkRole.Backdrop)
            return await RetryAsync(() => browser.GetCandidatesAsync(gameId, new(role), page, ct), ct);
        var candidates = new List<ProviderArtworkCandidate>();
        for (var count = 0; count < MaxPagesPerProvider; count++)
        {
            var result = await RetryAsync(() => browser.GetCandidatesAsync(gameId, new(role), page, ct), ct);
            if (result.IsError)
                return result.Errors;
            candidates.AddRange(result.Value.Items);
            if (result.Value.NextPage is not { } next || next <= page)
                break;
            page = next;
        }
        return new ProviderArtworkPage(candidates, null);
    }

    private static async Task<ErrorOr<T>> RetryAsync<T>(Func<Task<ErrorOr<T>>> request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await request();
            if (!result.IsError || attempt >= 2 || !result.Errors.Any(x =>
                x.Code == "Artwork.ProviderUnavailable"))
                return result;
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct);
        }
    }

    private static int Priority(string status) => status switch
    {
        "NeedsReview" => 5,
        "Failed" => 4,
        "NeedsMatch" => 3,
        "Unavailable" => 2,
        "Unlinked" or "ProviderUnconfigured" => 1,
        _ => 0
    };
}
