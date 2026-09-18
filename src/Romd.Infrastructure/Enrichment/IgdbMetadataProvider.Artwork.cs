using ErrorOr;
using System.Globalization;
using System.Text.Json;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Enrichment;

public sealed partial class IgdbMetadataProvider
{
    internal async Task<ErrorOr<ProviderArtworkPage>> BrowseArtworkAsync(string gameId, ProviderArtworkQuery query,
        int page, CancellationToken ct)
    {
        if (!long.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
            page != 0 || !Enum.IsDefined(query.Role) || query.Dimension is not null || query.Style is not null)
            return ArtworkProviderErrors.InvalidRequest();
        var mediaType = query.MediaType ?? query.Role switch
        {
            ArtworkRole.Poster => MediaType.Cover,
            ArtworkRole.Hero or ArtworkRole.Backdrop => MediaType.Background,
            _ => (MediaType?)null
        };
        if (mediaType is null || query.Role switch
            {
                ArtworkRole.Poster => mediaType != MediaType.Cover,
                ArtworkRole.Hero or ArtworkRole.Backdrop => mediaType is not (MediaType.Background or MediaType.Screenshot),
                _ => true
            })
            return ArtworkProviderErrors.InvalidRequest();
        await InitializeAsync(ct);
        if (!IsConfigured) return ArtworkProviderErrors.NotConfigured();
        try
        {
            await _rateLimiter.AcquireAsync(ProviderId, ct);
            await EnsureAccessTokenAsync(ct);
            var games = await SendIgdbRequestAsync("games",
                $"where id = {id}; fields slug,cover.image_id,cover.width,cover.height,artworks.image_id,artworks.width,artworks.height,screenshots.image_id,screenshots.width,screenshots.height; limit 1;", ct);
            if (games is null || games.Count == 0) return ArtworkProviderErrors.NotFound();
            var game = games[0];
            if (!game.TryGetProperty("slug", out var slug) || slug.GetString() is not { Length: > 0 } path ||
                !path.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return ArtworkProviderErrors.InvalidResponse();
            var items = new List<ProviderArtworkCandidate>();
            if (mediaType == MediaType.Cover && game.TryGetProperty("cover", out var cover)) Add(cover);
            if (mediaType != MediaType.Cover && game.TryGetProperty(mediaType == MediaType.Screenshot ? "screenshots" : "artworks", out var artwork) && artwork.ValueKind == JsonValueKind.Array)
                foreach (var image in artwork.EnumerateArray()) Add(image);
            return new ProviderArtworkPage(items, null);

            void Add(JsonElement image)
            {
                if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("image_id", out var key) ||
                    key.GetString() is not { Length: > 0 and <= 100 } assetId ||
                    !assetId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                    !image.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) ||
                    !image.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height) ||
                    width is <= 0 or > 16384 || height is <= 0 or > 16384) return;
                items.Add(new(gameId, assetId, query.Role, width, height, null,
                    new($"https://images.igdb.com/igdb/image/upload/t_720p/{assetId}.jpg"),
                    new($"https://images.igdb.com/igdb/image/upload/t_original/{assetId}.jpg"),
                    null, $"https://www.igdb.com/games/{path}", mediaType));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        { return ArtworkProviderErrors.Unavailable(); }
    }
}
