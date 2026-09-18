using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Platform;
using Romd.Storage;

namespace Romd.Infrastructure.Artwork;

/// <summary>Fills remaining roles from linked artwork providers after metadata-provider artwork.</summary>
public sealed class LinkedProviderArtworkService(AutomaticArtworkService metadataArtwork,
    IEnumerable<IArtworkProviderBrowser> browsers, IArtworkAssetSource source, ITitleProviderMatchService matches,
    IArtworkAcquisitionStore acquisition, IArtworkCurationRepository curation, IArtworkImageProcessor processor,
    IContentAddressableStore storage, IFileMutationLock locks, IOptions<EnrichmentOptions> options) : IAutomaticArtworkService
{
    public async Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
        IReadOnlyList<ProviderArtworkResult>? results = null)
    {
        await metadataArtwork.FillAsync(title, platform, context, ct, results);
        if (!options.Value.DownloadMedia) return;
        var settings = await acquisition.GetSettingsAsync(ct);
        var links = await matches.GetAsync(title.Id, ct);
        if (links.IsError) return;
        foreach (var browser in browsers.OrderBy(x => x.Capabilities.ProviderId, StringComparer.Ordinal))
        {
            var providerId = browser.Capabilities.ProviderId;
            var link = links.Value.SingleOrDefault(x => x.ProviderId == providerId);
            if (link is not { IsAvailable: true, State: "Confirmed", ExternalId: not null }) continue;
            foreach (var capability in browser.Capabilities.Roles)
            {
                var role = capability.Role;
                if (role == ArtworkRole.Backdrop) continue;
                var enabled = role switch
                {
                    ArtworkRole.Poster => settings.FillPosters,
                    ArtworkRole.Hero => settings.FillHeroes,
                    ArtworkRole.Logo => settings.FillLogos,
                    _ => false
                };
                var mediaType = role switch
                {
                    ArtworkRole.Poster => MediaType.Cover,
                    ArtworkRole.Hero => MediaType.Background,
                    ArtworkRole.Logo => MediaType.Logo,
                    _ => (MediaType?)null
                };
                if (!enabled || mediaType is null || !options.Value.MediaTypesToDownload.Contains(mediaType.Value)) continue;
                var states = await curation.GetStatesAsync(title.Id, ct);
                var gallery = await curation.GetGalleryAsync(title.Id, ct);
                if (states.IsError || gallery.IsError) continue;
                var selection = states.Value.Single(x => x.Role == role);
                if (selection.Mode == ArtworkSelectionMode.Pinned || selection.PendingJobId is not null ||
                    gallery.Value.Any(x => x.Role == role && x.IsEligible) ||
                    role == ArtworkRole.Poster && title.Media.Any(x => x.Type == MediaType.Cover)) continue;
                var candidates = await browser.GetCandidatesAsync(link.ExternalId, new(role), 0, ct);
                if (candidates.IsError) { await Record("Failed"); continue; }
                var published = false;
                var failed = false;
                var cancelledByCuration = false;
                foreach (var candidate in candidates.Value.Items.Where(x => ArtworkCandidateRanking.IsSuitable(role, x.Width, x.Height))
                    .OrderBy(x => ArtworkCandidateRanking.CropCost(role, x.Width, x.Height))
                    .ThenByDescending(x => Math.Min((long)x.Width * x.Height, 1920L * 1080))
                    .ThenBy(x => x.ProviderAssetId, StringComparer.Ordinal).Take(3))
                {
                    var downloaded = await source.DownloadAsync(providerId, link.ExternalId, candidate.ProviderAssetId, role, candidate.AssetUrl.AbsoluteUri, ct);
                    if (downloaded.IsError) { failed = true; continue; }
                    var image = await processor.ProcessAsync(downloaded.Value.Bytes, role, ct);
                    if (image.IsError || !ArtworkCandidateRanking.IsSuitable(role, image.Value.Width, image.Value.Height)) { failed = true; continue; }
                    var superseded = false;
                    await context.ExecuteOwnedMutationAsync(async token =>
                    {
                        if (!await acquisition.LockMissingAsync(title.Id, role, selection.Revision, token) ||
                            await matches.GetLinkRevisionAsync(title.Id, providerId, link.ExternalId, token) != link.Revision)
                        { superseded = true; return; }
                        var bytes = downloaded.Value.Bytes;
                        foreach (var hash in image.Value.Variants.Select(x => Hash(x.EncodedBytes)).Append(Hash(bytes)).Distinct().OrderBy(x => x.ToString(), StringComparer.Ordinal))
                            await locks.AcquireAsync(hash, token);
                        var original = await Store(bytes, image.Value.ContentType, image.Value.Width, image.Value.Height, "original", token);
                        var variants = new List<RetainedArtworkFile>();
                        foreach (var variant in image.Value.Variants)
                            variants.Add(await Store(variant.EncodedBytes, variant.ContentType, variant.Width, variant.Height, variant.Name, token));
                        await curation.StageLocalAssetAsync(title.Id, role, providerId,
                            new(original, variants, candidate.Attribution, candidate.SourcePageUrl), Guid.Empty, token, link.ExternalId, candidate.ProviderAssetId);
                        await acquisition.RecordAsync(title.Id, role, "Updated", providerId, token);
                    }, ct);
                    if (superseded) { cancelledByCuration = true; break; }
                    published = true;
                    break;
                }
                if (!published) await Record(cancelledByCuration ? "Superseded" : failed ? "Failed" : "Unavailable");
                Task Record(string status) => context.ExecuteOwnedMutationAsync(token => acquisition.RecordAsync(title.Id, role, status, providerId, token), ct);
            }
        }
    }

    private async Task<RetainedArtworkFile> Store(byte[] bytes, string type, int width, int height, string name, CancellationToken ct)
    {
        await using var stream = new MemoryStream(bytes, false);
        var stored = await storage.StoreAsync(stream, null, ct);
        return new(stored.Key.Hash, stored.Size, stored.CompressedSize, stored.IsCompressed, type, width, height, name);
    }
    private static Sha256 Hash(byte[] bytes) => Sha256.FromSpan(SHA256.HashData(bytes));
}
