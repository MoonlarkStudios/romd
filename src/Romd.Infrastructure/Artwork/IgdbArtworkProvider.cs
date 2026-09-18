using ErrorOr;
using System.Globalization;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Enrichment;

namespace Romd.Infrastructure.Artwork;

public sealed class IgdbArtworkProvider(IgdbMetadataProvider metadata, IHttpClientFactory clients) : IArtworkProvider
{
    public ArtworkProviderCapabilities Capabilities { get; } = new("igdb", "IGDB",
        [new(ArtworkRole.Poster, [], []), new(ArtworkRole.Hero, [], []), new(ArtworkRole.Backdrop, [], [])], MediaTypes: [MediaType.Cover, MediaType.Background, MediaType.Screenshot]);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var state = await ((IProviderIdentityAdapter)metadata).GetAvailabilityAsync(ct);
        return state.Enabled && state.Configured;
    }

    public async Task<ErrorOr<IReadOnlyList<ProviderArtworkGame>>> SearchAsync(string query, CancellationToken ct = default)
    {
        var result = await ((IProviderIdentityAdapter)metadata).SearchAsync(query, ct);
        if (result.IsError) return result.Errors;
        return result.Value.Select(game => new ProviderArtworkGame(game.Id, game.Name)).ToArray();
    }

    public Task<ErrorOr<ProviderArtworkPage>> GetCandidatesAsync(string providerGameId, ProviderArtworkQuery query,
        int page = 0, CancellationToken ct = default) => metadata.BrowseArtworkAsync(providerGameId, query, page, ct);

    public async Task<ErrorOr<DownloadedArtworkAsset>> DownloadAsync(string providerId, string providerGameId,
        string providerAssetId, ArtworkRole role, string trustedAssetUrl, CancellationToken ct = default)
    {
        if (!long.TryParse(providerGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
            role is not (ArtworkRole.Poster or ArtworkRole.Hero or ArtworkRole.Backdrop) || !ValidAssetId(providerAssetId) ||
            trustedAssetUrl != $"https://images.igdb.com/igdb/image/upload/t_original/{providerAssetId}.jpg")
            return ArtworkProviderErrors.InvalidRequest();
        var result = await DownloadImageAsync(providerId, role, trustedAssetUrl, "t_original", ct);
        if (result.IsError) return result.Errors;
        var identity = await ((IProviderIdentityAdapter)metadata).ResolveAsync(providerGameId, ct);
        return result.Value with { SourcePageUrl = identity.IsError ? null : identity.Value.Url };
    }

    public Task<ErrorOr<DownloadedArtworkAsset>> DownloadPreviewAsync(string providerId, ArtworkRole role,
        string trustedPreviewUrl, CancellationToken ct = default) => DownloadImageAsync(providerId, role, trustedPreviewUrl, "t_720p", ct);

    private async Task<ErrorOr<DownloadedArtworkAsset>> DownloadImageAsync(string providerId, ArtworkRole role,
        string url, string size, CancellationToken ct)
    {
        var prefix = $"https://images.igdb.com/igdb/image/upload/{size}/";
        if (providerId != Capabilities.ProviderId || role is not (ArtworkRole.Poster or ArtworkRole.Hero or ArtworkRole.Backdrop) ||
            !url.StartsWith(prefix, StringComparison.Ordinal) ||
            !url.EndsWith(".jpg", StringComparison.Ordinal) || !ValidAssetId(url[prefix.Length..^4]))
            return ArtworkProviderErrors.InvalidRequest();
        if (!await IsAvailableAsync(ct)) return ArtworkProviderErrors.NotConfigured();
        using var client = clients.CreateClient(AutomaticArtworkService.HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        const int limit = 32 * 1024 * 1024;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return ArtworkProviderErrors.Unavailable();
            if (response.Content.Headers.ContentLength > limit) return ArtworkProviderErrors.InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > limit) return ArtworkProviderErrors.InvalidResponse();
                output.Write(buffer, 0, read);
            }
            return new DownloadedArtworkAsset(output.ToArray(), response.Content.Headers.ContentType?.MediaType, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ArtworkProviderErrors.Unavailable(); }
        catch (HttpRequestException) { return ArtworkProviderErrors.Unavailable(); }
        catch (IOException) { return ArtworkProviderErrors.Unavailable(); }
    }

    private static bool ValidAssetId(string value) => value.Length is > 0 and <= 100 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
