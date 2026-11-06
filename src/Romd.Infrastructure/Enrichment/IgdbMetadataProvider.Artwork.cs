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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await InitializeAsync(deadline.Token);
            if (!IsConfigured)
                return ArtworkProviderErrors.NotConfigured();
            await _rateLimiter.AcquireAsync(ProviderId, deadline.Token);
            var response = await ReadArtworkResponseAsync(
                $"where id = {id}; fields slug,cover.image_id,cover.width,cover.height,artworks.image_id,artworks.width,artworks.height,screenshots.image_id,screenshots.width,screenshots.height; limit 1;", deadline.Token);
            if (response.IsError)
                return response.Errors;
            using var document = response.Value;
            var games = document.RootElement;
            if (games.ValueKind != JsonValueKind.Array || games.GetArrayLength() > 1)
                return ArtworkProviderErrors.InvalidResponse();
            if (games.GetArrayLength() == 0)
                return ArtworkProviderErrors.NotFound();
            var game = games[0];
            if (game.ValueKind != JsonValueKind.Object)
                return ArtworkProviderErrors.InvalidResponse();
            if (!game.TryGetProperty("slug", out var slug) || slug.GetString() is not { Length: > 0 } path ||
                !path.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return ArtworkProviderErrors.InvalidResponse();
            // This is a defensive adapter budget on the raw response, before selecting a role or filtering images.
            // IGDB supplies no stable candidate keyset or promised maximum game-image collection.
            var rawCount = 0;
            if (game.TryGetProperty("cover", out var rawCover) && rawCover.ValueKind != JsonValueKind.Null)
            {
                if (rawCover.ValueKind != JsonValueKind.Object)
                    return ArtworkProviderErrors.InvalidResponse();
                rawCount++;
            }
            foreach (var kind in new[] { "artworks", "screenshots" })
                if (game.TryGetProperty(kind, out var images) && images.ValueKind != JsonValueKind.Null)
                {
                    if (images.ValueKind != JsonValueKind.Array)
                        return ArtworkProviderErrors.InvalidResponse();
                    rawCount += images.GetArrayLength();
                    if (rawCount > 500)
                        return ArtworkProviderErrors.InvalidResponse();
                }
            var items = new List<ProviderArtworkCandidate>();
            if (mediaType == MediaType.Cover && game.TryGetProperty("cover", out var cover))
                Add(cover);
            if (mediaType != MediaType.Cover && game.TryGetProperty(mediaType == MediaType.Screenshot ? "screenshots" : "artworks", out var artwork) && artwork.ValueKind == JsonValueKind.Array)
                foreach (var image in artwork.EnumerateArray())
                    Add(image);
            return new ProviderArtworkPage(items, null);

            void Add(JsonElement image)
            {
                if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("image_id", out var key) ||
                    key.GetString() is not { Length: > 0 and <= 100 } assetId ||
                    !assetId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                    !image.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) ||
                    !image.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height) ||
                    width is <= 0 or > 16384 || height is <= 0 or > 16384)
                    return;
                items.Add(new(gameId, assetId, query.Role, width, height, null,
                    new($"https://images.igdb.com/igdb/image/upload/t_720p/{assetId}.jpg"),
                    new($"https://images.igdb.com/igdb/image/upload/t_original/{assetId}.jpg"),
                    null, $"https://www.igdb.com/games/{path}", mediaType));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { return ArtworkProviderErrors.InvalidResponse(); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        { return ArtworkProviderErrors.Unavailable(); }
    }

    /// <summary>Bounded game previews for artwork and identity discovery; metadata enrichment uses its own transport.</summary>
    private async Task<ErrorOr<JsonDocument>> ReadArtworkResponseAsync(string query, CancellationToken ct)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        using var response = await SendAuthorizedAsync("games", query, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retryAt = response.Headers.RetryAfter?.Date ??
                _timeProvider.GetUtcNow().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            return ArtworkProviderErrors.RateLimited(retryAt);
        }
        if (!response.IsSuccessStatusCode)
            return ArtworkProviderErrors.Unavailable();
        if (response.Content.Headers.ContentLength > maximumBytes)
            return ArtworkProviderErrors.InvalidResponse();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > maximumBytes)
                return ArtworkProviderErrors.InvalidResponse();
            body.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(body.ToArray());
    }
}
