using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Artwork;

public sealed class AutomaticArtworkService(IArtworkAcquisitionStore acquisition, IArtworkCurationRepository curation,
    IArtworkImageProcessor processor, IFileStorageService files, IUnitOfWork unitOfWork, IEnumerable<IMetadataProvider> providers,
    IEnrichmentContextFactory contexts,
    IOptions<EnrichmentOptions> options, IHttpClientFactory clients, ILogger<AutomaticArtworkService> logger,
    ITitleProviderMatchService? matches = null)
    : IAutomaticArtworkService
{
    public const string HttpClientName = "AutomaticIgdbArtwork";
    private const int MaximumBytes = 20 * 1024 * 1024;
    private static readonly ArtworkRole[] SupportedRoles = [ArtworkRole.Poster, ArtworkRole.Hero, ArtworkRole.Backdrop];

    public async Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
        IReadOnlyList<ProviderArtworkResult>? results = null)
    {
        if (matches is not null)
        {
            await matches.DiscoverAsync(title.Id, context, ct);
            if (await matches.IsSuppressedAsync(title.Id, "igdb", ct))
                return;
        }
        var settings = await acquisition.GetSettingsAsync(ct);
        var states = await curation.GetStatesAsync(title.Id, ct);
        if (states.IsError)
            return;
        var gallery = await curation.GetGalleryAsync(title.Id, ct);
        if (gallery.IsError)
            return;
        var originalProviderGameId = await acquisition.GetProviderGameIdAsync(title.Id, "igdb", ct);
        var provider = providers.SingleOrDefault(x => x.ProviderId == "igdb");
        if (provider is not null)
            await provider.InitializeAsync(ct);
        var available = provider is { IsConfigured: true } &&
            (provider.UsesRuntimeConfiguration || options.Value.IsProviderEnabled(provider.ProviderId));
        EnrichmentResult? discovered = results?.FirstOrDefault(x => x.ProviderId == "igdb")?.Result;
        var discoveryAttempted = results is not null;

        // IGDB exposes game covers and artworks, but not a game-logo asset collection.
        foreach (var role in SupportedRoles)
        {
            ct.ThrowIfCancellationRequested();
            var state = states.Value.Single(x => x.Role == role);
            if (state.Mode == ArtworkSelectionMode.Empty)
            { await Record("LeftEmpty"); continue; }
            var present = state.Mode == ArtworkSelectionMode.Pinned || state.PendingJobId is not null ||
                gallery.Value.Any(x => x.Role == role && x.IsEligible);
            if (present)
            { await Record("AlreadyPresent"); continue; }
            if (!options.Value.DownloadMedia ||
                !options.Value.MediaTypesToDownload.Contains(role == ArtworkRole.Poster ? MediaType.Cover : MediaType.Background) ||
                !(role switch { ArtworkRole.Poster => settings.FillPosters, ArtworkRole.Backdrop => settings.FillBackdrops, _ => settings.FillHeroes }))
            { await Record("Disabled"); continue; }
            if (!available)
            { await Record("ProviderDisabled"); continue; }

            // Each provider's own match must be trusted. Another provider's confidence
            // or confirmed association cannot authorize this provider's artwork.
            var link = title.GetExternalId("igdb");
            if (!discoveryAttempted)
                await Discover();
            var trustedResult = discovered is { Outcome: EnrichmentOutcome.Found } &&
                (link?.IsConfirmed == true ? link.ExternalId == discovered.ExternalId :
                    (link is null || link.ExternalId == discovered.ExternalId) && discovered.MatchConfidence >= options.Value.MinimumAutoEnrichConfidence);
            if (!trustedResult)
            { await Record(discovered?.Outcome == EnrichmentOutcome.Error ? "Failed" : "NeedsMatch"); continue; }

            // Dimensions cannot establish that artwork is free of logos or edition branding.
            // Respect an administrator's explicit review preference.
            if (role == ArtworkRole.Backdrop && settings.ReviewBackdrops)
            {
                await Record(discovered!.Artwork.Any(x => x.Role == role && ArtworkCandidateRanking.IsSuitable(role, x.Width, x.Height))
                    ? "NeedsReview" : "Unavailable");
                continue;
            }

            var hadFailure = false;
            var published = false;
            var superseded = false;
            var gameId = discovered!.ExternalId;
            // Reuse only images still belonging to this provider game. A historical
            // background may predate an admin correcting the title's association.
            var local = title.Media.Where(x => role != ArtworkRole.Backdrop && x.SourceId == "igdb" &&
                x.Type == (role == ArtworkRole.Poster ? MediaType.Cover : MediaType.Background) &&
                x.SourceUrl is not null && discovered.Artwork.Any(candidate => candidate.Role == role &&
                    Path.GetFileNameWithoutExtension(x.SourceUrl) == candidate.AssetId))
                .OrderBy(x => x.Id).Take(3);
            foreach (var media in local)
            {
                try
                {
                    await using var stream = await files.RetrieveByIdAsync(media.FileId, ct);
                    if (stream is not null && await ReadBoundedAsync(stream, ct) is { } bytes)
                        await TryPublish(bytes, Path.GetFileNameWithoutExtension(media.SourceUrl));
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                { hadFailure = true; logger.LogWarning(ex, "Could not read retained artwork for title {TitleId}", title.Id); }
                if (published || superseded)
                    break;
            }
            if (!published && !superseded)
            {
                if (!discoveryAttempted)
                    await Discover();
                trustedResult = discovered is { Outcome: EnrichmentOutcome.Found } &&
                    (link?.IsConfirmed == true ? link.ExternalId == discovered.ExternalId :
                        discovered.MatchConfidence >= options.Value.MinimumAutoEnrichConfidence);
                if (trustedResult)
                {
                    gameId = discovered!.ExternalId;
                    var candidates = discovered.Artwork.Where(x => x.Role == role && ArtworkCandidateRanking.IsSuitable(role, x.Width, x.Height))
                        .OrderBy(x => role == ArtworkRole.Backdrop ? ArtworkCandidateRanking.BackdropPreference(x.Width, x.Height) : ArtworkCandidateRanking.CropCost(role, x.Width, x.Height))
                        .ThenByDescending(x => Math.Min((long)x.Width * x.Height, 1920L * 1080))
                        .ThenBy(x => x.AssetId, StringComparer.Ordinal).Take(3);
                    foreach (var candidate in candidates)
                    {
                        byte[]? bytes;
                        try
                        { bytes = await DownloadAsync(candidate.Url, ct); }
                        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                        { hadFailure = true; logger.LogWarning(ex, "Could not download {Role} for title {TitleId}", role, title.Id); continue; }
                        if (bytes is null)
                        { hadFailure = true; continue; }
                        await TryPublish(bytes, candidate.AssetId);
                        if (published || superseded)
                            break;
                    }
                }
                else if (discovered?.Outcome == EnrichmentOutcome.Error)
                    hadFailure = true;
            }
            if (!published)
                await Record(superseded ? "Superseded" : hadFailure ? "Failed" : "Unavailable");

            async Task Record(string status) => await context.ExecuteOwnedMutationAsync(
                token => acquisition.RecordAsync(title.Id, role, status, status == "Updated" ? "igdb" : null, token), ct);

            async Task TryPublish(byte[] bytes, string? assetId)
            {
                var image = await processor.ProcessAsync(bytes, role, ct);
                if (image.IsError)
                { hadFailure = true; return; }
                if (!ArtworkCandidateRanking.IsSuitable(role, image.Value.Width, image.Value.Height))
                    return;
                await using var staged = await PreparedArtwork.StageAsync(files, bytes, image.Value, ct);
                await context.ExecuteOwnedMutationAsync(async token =>
                {
                    if (!await acquisition.LockMissingAsync(title.Id, role, state.Revision, token) ||
                        await acquisition.GetProviderGameIdAsync(title.Id, "igdb", token) != originalProviderGameId)
                    { superseded = true; return; }
                    var publication = await files.PublishStagedAsync(staged.Files, token);
                    if (publication.RequiresFlush)
                        await unitOfWork.FlushAsync(token);
                    var content = staged.Retain(publication.ReadAfterFlush(), "IGDB", null);
                    await curation.StageLocalAssetAsync(title.Id, role, "igdb", content, token, gameId, assetId);
                    await acquisition.RecordAsync(title.Id, role, "Updated", "igdb", token);
                }, ct);
                published = !superseded;
            }
        }

        async Task Discover()
        {
            discoveryAttempted = true;
            var enrichmentContext = await contexts.CreateAsync(title, platform, ct);
            try
            {
                discovered = await provider!.EnrichSingleAsync(enrichmentContext with { ExistingExternalId = title.GetExternalId("igdb")?.ExternalId }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Artwork discovery failed for title {TitleId}", title.Id);
                discovered = EnrichmentResult.Error("Artwork discovery failed.");
            }
        }
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "images.igdb.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/igdb/image/upload/", StringComparison.Ordinal) || uri.Query.Length != 0)
            return null;
        using var client = clients.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await ReadBoundedAsync(stream, timeout.Token);
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumBytes)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }
}
