using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ErrorOr;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Artwork.SteamGridDb;

/// <summary>
/// Read-only SteamGridDB adapter. All transport redirects are disabled and CDN redirects
/// are explicitly revalidated; credentials are attached only to fixed-origin API requests.
/// </summary>
public sealed partial class SteamGridDbArtworkProvider : IArtworkProvider, IDisposable
{
    private const string ApiOrigin = "https://www.steamgriddb.com/api/v2/";
    private const int PageSize = 50;
    private const int MaxPages = 100;
    private readonly ISteamGridDbCredentials _credentials;
    private readonly HttpClient _http;
    private readonly SteamGridDbRateGate _rateGate;

    public SteamGridDbArtworkProvider(ISteamGridDbCredentials credentials, SteamGridDbRateGate rateGate)
        : this(credentials, new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, rateGate) { }

    /// <summary>The injected handler is for controlled tests and must never automatically redirect.</summary>
    public SteamGridDbArtworkProvider(ISteamGridDbCredentials credentials, HttpMessageHandler handler, SteamGridDbRateGate? rateGate = null)
    {
        _credentials = credentials;
        _rateGate = rateGate ?? new SteamGridDbRateGate();
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ArtworkProviderCapabilities Capabilities { get; } = new("steamgriddb", "SteamGridDB",
    [
        new(ArtworkRole.Poster, ["600x900", "342x482", "660x930"], ["alternate", "blurred", "white_logo", "material", "no_logo"]),
        new(ArtworkRole.Hero, ["1920x620", "3840x1240", "1600x650"], ["alternate", "blurred", "material"]),
        new(ArtworkRole.Logo, [], ["official", "white", "black", "custom"])
    ], MediaTypes: [MediaType.Cover, MediaType.Background, MediaType.Logo]);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await _credentials.GetApiKeyAsync(ct));

    public async Task<ErrorOr<IReadOnlyList<ProviderArtworkGame>>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return ArtworkProviderErrors.InvalidRequest();
        var response = await ApiAsync($"search/autocomplete/{Uri.EscapeDataString(query.Trim())}", ct);
        if (response.IsError) return response.Errors;
        using var json = response.Value;
        try
        {
            IReadOnlyList<ProviderArtworkGame> games = json.RootElement.GetProperty("data").EnumerateArray()
                .Take(100).Select(game => new ProviderArtworkGame(Id(game), game.GetProperty("name").GetString()!))
                .Where(game => !string.IsNullOrWhiteSpace(game.Name) && game.Name.Length <= 500).ToArray();
            return ErrorOrFactory.From(games);
        }
        catch (Exception exception) when (IsInvalidJson(exception)) { return ArtworkProviderErrors.InvalidResponse(); }
    }

    public async Task<ErrorOr<ProviderArtworkPage>> GetCandidatesAsync(string providerGameId, ProviderArtworkQuery query,
        int page = 0, CancellationToken ct = default)
    {
        var response = await ReadPageAsync(providerGameId, query, page, ct);
        if (response.IsError) return response.Errors;
        return new ProviderArtworkPage(response.Value.Items.Select(item => item.Candidate).ToArray(), response.Value.NextPage);
    }

    public async Task<ErrorOr<DownloadedArtworkAsset>> DownloadAsync(string providerId, string providerGameId,
        string providerAssetId, ArtworkRole role, string trustedAssetUrl, CancellationToken ct = default)
    {
        // This location is accepted only from a verified server-issued candidate and persisted
        // with the durable request. Repeat all transport checks when that request executes.
        if (providerId != "steamgriddb" || !ValidId(providerGameId) || !ValidId(providerAssetId) || !Capabilities.Roles.Any(capability => capability.Role == role) ||
            !Uri.TryCreate(trustedAssetUrl, UriKind.Absolute, out var uri) || !AllowedAsset(uri) ||
            !uri.AbsolutePath.StartsWith(AssetPrefix(role), StringComparison.Ordinal))
            return ArtworkProviderErrors.InvalidRequest();
        var bytes = await ReadAsync(uri, null, 32 * 1024 * 1024, ct);
        if (bytes.IsError) return bytes.Errors;
        return new DownloadedArtworkAsset(bytes.Value.Bytes, bytes.Value.ContentType, null,
            $"https://www.steamgriddb.com/{AssetPageCategory(role)}/{providerAssetId}");
    }

    private async Task<ErrorOr<AssetPage>> ReadPageAsync(string gameId, ProviderArtworkQuery query, int page, CancellationToken ct)
    {
        var capability = Capabilities.Roles.SingleOrDefault(role => role.Role == query.Role);
        if (!ValidId(gameId) || page < 0 || page >= MaxPages || capability is null ||
            (query.Dimension is not null && !capability.Dimensions.Contains(query.Dimension)) ||
            (query.Style is not null && !capability.Styles.Contains(query.Style))) return ArtworkProviderErrors.InvalidRequest();
        if (query.MediaType is not null && query.MediaType != HistoricalMediaType(query.Role))
            return ArtworkProviderErrors.InvalidRequest();
        var dimensions = query.Dimension ?? string.Join(',', capability.Dimensions);
        // SteamGridDB rejects JPEG as a logo filter; only grids and heroes support it.
        var mimes = query.Role == ArtworkRole.Logo ? "image/png,image/webp" : "image/png,image/jpeg,image/webp";
        var path = $"{Category(query.Role)}/game/{gameId}?types=static&mimes={mimes}&nsfw=false&humor=false&epilepsy=false&limit={PageSize}&page={page}";
        if (dimensions.Length > 0) path += $"&dimensions={Uri.EscapeDataString(dimensions)}";
        if (query.Style is not null) path += $"&styles={Uri.EscapeDataString(query.Style)}";
        var response = await ApiAsync(path, ct);
        if (response.IsError) return response.Errors;
        using var json = response.Value;
        try
        {
            var root = json.RootElement;
            var rows = root.GetProperty("data");
            if (rows.GetArrayLength() > PageSize) return ArtworkProviderErrors.InvalidResponse();
            var items = new List<Asset>();
            foreach (var item in rows.EnumerateArray())
            {
                var assetId = Id(item);
                if (!Uri.TryCreate(item.GetProperty("url").GetString(), UriKind.Absolute, out var assetUrl) ||
                    !Uri.TryCreate(item.GetProperty("thumb").GetString(), UriKind.Absolute, out var previewUrl) ||
                    !AllowedAsset(assetUrl) || !AllowedAsset(previewUrl) ||
                    !assetUrl.AbsolutePath.StartsWith(AssetPrefix(query.Role), StringComparison.Ordinal) ||
                    !previewUrl.AbsolutePath.StartsWith(PreviewPrefix(query.Role), StringComparison.Ordinal))
                    return ArtworkProviderErrors.InvalidResponse();
                var width = item.GetProperty("width").GetInt32();
                var height = item.GetProperty("height").GetInt32();
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384) continue;
                var style = Style(item);
                var author = item.TryGetProperty("author", out var a) && a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (author?.Length > 500 || style?.Length > 100) return ArtworkProviderErrors.InvalidResponse();
                var candidate = new ProviderArtworkCandidate(gameId, assetId, query.Role, width, height, style,
                    previewUrl, assetUrl, author, $"https://www.steamgriddb.com/{AssetPageCategory(query.Role)}/{assetId}");
                items.Add(new Asset(candidate, assetUrl));
            }
            var count = rows.GetArrayLength();
            var hasNext = count == PageSize;
            if (root.TryGetProperty("total", out var total) && total.TryGetInt64(out var totalCount))
                hasNext = (long)(page + 1) * PageSize < totalCount;
            return new AssetPage(items, hasNext && page + 1 < MaxPages ? page + 1 : null);
        }
        catch (Exception exception) when (IsInvalidJson(exception) || exception is UriFormatException)
        { return ArtworkProviderErrors.InvalidResponse(); }
    }

    public async Task<ErrorOr<DownloadedArtworkAsset>> DownloadPreviewAsync(string providerId, ArtworkRole role,
        string trustedPreviewUrl, CancellationToken ct = default)
    {
        if (!Capabilities.Roles.Any(capability => capability.Role == role)) return ArtworkProviderErrors.InvalidRequest();
        var prefix = PreviewPrefix(role);
        if (providerId != "steamgriddb" ||
            !Uri.TryCreate(trustedPreviewUrl, UriKind.Absolute, out var uri) || !AllowedAsset(uri) ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return ArtworkProviderErrors.InvalidRequest();
        var result = await ReadAsync(uri, null, 4 * 1024 * 1024, ct);
        if (result.IsError) return result.Errors;
        return new DownloadedArtworkAsset(result.Value.Bytes, result.Value.ContentType, null, null);
    }

    private async Task<ErrorOr<JsonDocument>> ApiAsync(string path, CancellationToken ct)
    {
        var key = await _credentials.GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            return ArtworkProviderErrors.NotConfigured();
        var response = await ReadAsync(new Uri(ApiOrigin + path), key, 2 * 1024 * 1024, ct);
        if (response.IsError) return response.Errors;
        try
        {
            var json = JsonDocument.Parse(response.Value.Bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                json.Dispose();
                return ArtworkProviderErrors.InvalidResponse();
            }
            return json;
        }
        catch (JsonException) { return ArtworkProviderErrors.InvalidResponse(); }
    }

    private async Task<ErrorOr<Body>> ReadAsync(Uri uri, string? key, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_rateGate.IsLimited) return ArtworkProviderErrors.RateLimited();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var assetPrefix = "/" + uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() + "/";
        try
        {
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                if (key is null && (!AllowedAsset(uri) || !uri.AbsolutePath.StartsWith(assetPrefix, StringComparison.Ordinal)))
                    return ArtworkProviderErrors.InvalidResponse();
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                    _rateGate.Defer(delay);
                    return ArtworkProviderErrors.RateLimited();
                }
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (key is not null || redirect == 3 || response.Headers.Location is not { } location)
                        return ArtworkProviderErrors.InvalidResponse();
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound) return ArtworkProviderErrors.NotFound();
                if (!response.IsSuccessStatusCode) return ArtworkProviderErrors.Unavailable();
                if (response.Content.Headers.ContentLength > limit) return ArtworkProviderErrors.InvalidResponse();
                using var output = new MemoryStream();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (output.Length + read > limit) return ArtworkProviderErrors.InvalidResponse();
                    output.Write(buffer, 0, read);
                }
                return new Body(output.ToArray(), response.Content.Headers.ContentType?.MediaType);
            }
            return ArtworkProviderErrors.InvalidResponse();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ArtworkProviderErrors.Unavailable(); }
        catch (HttpRequestException) { return ArtworkProviderErrors.Unavailable(); }
        catch (IOException) { return ArtworkProviderErrors.Unavailable(); }
    }

    private static bool AllowedAsset(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("cdn2.steamgriddb.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.AbsolutePath.StartsWith("/grid/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/hero/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/logo/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/thumb/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/hero_thumb/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/logo_thumb/", StringComparison.Ordinal)) &&
        new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
    private static bool ValidId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
    private static string Id(JsonElement element)
    {
        var value = element.GetProperty("id").GetInt64();
        if (value <= 0) throw new JsonException();
        return value.ToString(CultureInfo.InvariantCulture);
    }
    private static string? Style(JsonElement element)
    {
        if (!element.TryGetProperty("style", out var style)) return null;
        if (style.ValueKind == JsonValueKind.String) return style.GetString();
        if (style.ValueKind != JsonValueKind.Array || style.GetArrayLength() > 10) throw new JsonException();
        string? first = null;
        foreach (var value in style.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw new JsonException();
            first ??= value.GetString();
        }
        return first;
    }
    private static string Category(ArtworkRole role) => role switch
    {
        ArtworkRole.Poster => "grids",
        ArtworkRole.Hero => "heroes",
        ArtworkRole.Logo => "logos",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static string AssetPageCategory(ArtworkRole role) => role switch
    {
        ArtworkRole.Poster => "grid",
        ArtworkRole.Hero => "hero",
        ArtworkRole.Logo => "logo",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static string AssetPrefix(ArtworkRole role) => $"/{AssetPageCategory(role)}/";
    private static string PreviewPrefix(ArtworkRole role) => role switch
    {
        ArtworkRole.Poster => "/thumb/",
        ArtworkRole.Hero => "/hero_thumb/",
        ArtworkRole.Logo => "/logo_thumb/",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static MediaType HistoricalMediaType(ArtworkRole role) => role switch
    {
        ArtworkRole.Poster => MediaType.Cover,
        ArtworkRole.Hero => MediaType.Background,
        ArtworkRole.Logo => MediaType.Logo,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static bool IsInvalidJson(Exception exception) => exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException;
    public void Dispose() => _http.Dispose();
    private sealed record Asset(ProviderArtworkCandidate Candidate, Uri AssetUrl);
    private sealed record AssetPage(IReadOnlyList<Asset> Items, int? NextPage);
    private sealed record Body(byte[] Bytes, string? ContentType);
}
