using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;

namespace Romd.Infrastructure.Providers.Igdb;

/// <summary>
///     IGDB metadata provider for fetching game metadata.
///     Requires Twitch OAuth credentials to be configured.
///     Platform translation comes from the platform provider mappings
///     (PlatformAliases) — seeded by <see cref="Persistence.ReferenceData.SharedReferenceDataSeeder" />
///     and user-manageable per platform.
/// </summary>
public sealed partial class IgdbMetadataProvider : IMetadataProvider
{
    private const string TwitchAuthUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbApiUrl = "https://api.igdb.com/v4";
    private const string GameFields =
        "id, name, summary, cover.url, cover.width, cover.height, first_release_date, " +
        "involved_companies.company.name, involved_companies.developer, involved_companies.publisher, " +
        "genres.name, game_modes.name, total_rating, " +
        "age_ratings.id, age_ratings.organization.name, age_ratings.rating_category.rating, " +
        "age_ratings.rating_content_descriptions.description, age_ratings.synopsis, " +
        "screenshots.url, artworks.url, artworks.width, artworks.height";

    internal const int MaxBatchSize = 10;

    private readonly IPlatformAliasRepository _aliasRepository;
    private readonly MatchConfidenceCalculator _confidenceCalculator;

    private readonly HttpClient _httpClient;
    private readonly ILogger<IgdbMetadataProvider> _logger;
    private IgdbProviderOptions _options;
    private readonly IgdbProviderSettingsService? _settingsService;
    private bool _initialized;
    private bool _enabled = true;
    private readonly IProviderRateLimiter _rateLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IgdbAccessTokenCache _accessTokens;

    private IReadOnlyDictionary<string, int>? _platformMap;

    public IgdbMetadataProvider(
        HttpClient httpClient,
        IOptions<IgdbProviderOptions> options,
        IProviderRateLimiter rateLimiter,
        MatchConfidenceCalculator confidenceCalculator,
        IPlatformAliasRepository aliasRepository,
        TimeProvider timeProvider,
        ILogger<IgdbMetadataProvider> logger,
        IgdbAccessTokenCache accessTokens,
        IgdbProviderSettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        _httpClient = httpClient;
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _confidenceCalculator = confidenceCalculator;
        _aliasRepository = aliasRepository;
        _timeProvider = timeProvider;
        _logger = logger;
        _accessTokens = accessTokens;
    }

    /// <summary>
    ///     Loads the short-name → IGDB platform ID map from provider mappings,
    ///     once per provider instance (scoped to a single enrichment run).
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<string, int>> GetPlatformMapAsync(CancellationToken ct)
    {
        if (_platformMap is not null)
        {
            return _platformMap;
        }

        var mappings = await _aliasRepository.GetProviderMappingsByShortNameAsync(ProviderId, ct);
        var map = new Dictionary<string, int>(mappings.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (shortName, externalId) in mappings)
        {
            if (int.TryParse(externalId, out int igdbId))
            {
                map[shortName] = igdbId;
            }
            else
            {
                _logger.LogWarning(
                    "Ignoring non-numeric IGDB platform mapping '{Value}' for platform '{Platform}'",
                    externalId,
                    shortName);
            }
        }

        _platformMap = map;
        return map;
    }

    public string ProviderId => "igdb";
    public string DisplayName => "IGDB";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _settingsService is null)
        {
            return;
        }
        var settings = await _settingsService.GetRuntimeAsync(ct);
        _options = new IgdbProviderOptions { ClientId = settings.ClientId, ClientSecret = settings.ClientSecret };
        _enabled = settings.Enabled && settings.HasCredentials;
        _initialized = true;
    }

    public bool UsesRuntimeConfiguration => _settingsService is not null;

    public bool IsConfigured =>
        _enabled &&
        !string.IsNullOrWhiteSpace(_options.ClientId) &&
        !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async IAsyncEnumerable<(MetadataFetchRequest Request, EnrichmentResult Result)> FetchAsync(
        IAsyncEnumerable<MetadataFetchRequest> requests,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!IsConfigured)
        {
            await foreach (var request in requests.WithCancellation(ct))
            {
                yield return (request, EnrichmentResult.Error("IGDB is disabled or not configured. Check Admin Settings > Metadata Providers."));
            }
            yield break;
        }

        var batch = new List<MetadataFetchRequest>(MaxBatchSize);
        await foreach (var request in requests.WithCancellation(ct))
        {
            batch.Add(request);
            if (batch.Count < MaxBatchSize)
                continue;

            await foreach (var pair in FetchBatchAsync(batch, ct))
                yield return pair;
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await foreach (var pair in FetchBatchAsync(batch, ct))
                yield return pair;
        }
    }

    private async IAsyncEnumerable<(MetadataFetchRequest Request, EnrichmentResult Result)> FetchBatchAsync(
        IReadOnlyList<MetadataFetchRequest> batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var query = new StringBuilder();
        var gameIds = new long[batch.Count];
        for (var index = 0; index < batch.Count; index++)
        {
            if (!long.TryParse(batch[index].GameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                continue;

            gameIds[index] = id;
            query.AppendLine(CultureInfo.InvariantCulture, $$"""
                query games "q{{index}}" {
                    fields {{GameFields}};
                    where id = {{id}};
                    limit 1;
                };
                """);
        }

        ILookup<string, MultiQueryResponse>? responses = null;
        bool authenticationRejected = false;
        if (query.Length > 0)
        {
            try
            {
                await _rateLimiter.AcquireAsync(ProviderId, ct);
                responses = (await SendMultiQueryAsync(query.ToString(), ct)).ToLookup(x => x.Name, StringComparer.Ordinal);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // The transport already retried with fresh credentials. Repeating the batch
                // as individual requests would multiply a permanent authentication failure.
                authenticationRejected = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                _logger.LogWarning(ex, "IGDB multi-query failed. Retrying the same game IDs individually.");
            }
        }

        for (var index = 0; index < batch.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var request = batch[index];
            var id = gameIds[index];
            if (id == 0)
                yield return (request, EnrichmentResult.Error("IGDB requires a positive numeric game ID."));
            else if (authenticationRejected)
                yield return (request, EnrichmentResult.Error("IGDB authentication failed."));
            else if (responses is null)
                yield return (request, await FetchGameAsync(request.Context, id, ct));
            else
            {
                var matches = responses[$"q{index}"];
                var result = matches.Count() > 1
                    ? EnrichmentResult.Error("IGDB returned duplicate batch results for a game ID.")
                    : ReadFetchedGame(request.Context, id, matches.SingleOrDefault()?.Result);
                yield return (request, result);
            }
        }
    }

    private async Task<EnrichmentResult> FetchGameAsync(EnrichmentContext context, long id, CancellationToken ct)
    {
        try
        {
            await _rateLimiter.AcquireAsync(ProviderId, ct);
            var query = FormattableString.Invariant($"fields {GameFields}; where id = {id}; limit 1;");
            return ReadFetchedGame(context, id, await SendIgdbRequestAsync("games", query, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not fetch IGDB game {GameId}", id);
            return EnrichmentResult.Error("IGDB game lookup failed.");
        }
    }

    private EnrichmentResult ReadFetchedGame(EnrichmentContext context, long id, IReadOnlyList<JsonElement>? games)
    {
        if (games is not { Count: > 0 })
            return EnrichmentResult.NotFound();
        if (games.Count != 1 || games[0].ValueKind != JsonValueKind.Object ||
            !games[0].TryGetProperty("id", out var returnedId) || returnedId.ValueKind != JsonValueKind.Number ||
            !returnedId.TryGetInt64(out var actualId) || actualId != id)
            return EnrichmentResult.Error("IGDB returned a game that does not match the requested ID.");

        try
        {
            return MapToResult(context, games[0]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            _logger.LogWarning(ex, "Could not read IGDB game {GameId}", id);
            return EnrichmentResult.Error("IGDB returned invalid game metadata.");
        }
    }

    private async Task<List<MultiQueryResponse>> SendMultiQueryAsync(
        string multiQuery,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync("multiquery", multiQuery,
            HttpCompletionOption.ResponseContentRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogDebug("IGDB multi-query response ({Length} chars): {Body}",
            body.Length, body.Length > 2000 ? body[..2000] + "..." : body);

        return JsonSerializer.Deserialize<List<MultiQueryResponse>>(body) ?? [];
    }

    private async Task<(string ClientId, IgdbAccessTokenCache.AccessToken Token)> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var options = _options;
        string clientId = options.ClientId!;
        string clientSecret = options.ClientSecret!;
        var token = await _accessTokens.GetAsync(clientId, clientSecret, async ct =>
        {
            using var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials"
            });

            using var response = await _httpClient.PostAsync(TwitchAuthUrl, tokenRequest, ct);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(ct)
                ?? throw new InvalidOperationException("Failed to parse Twitch token response");
            return (tokenResponse.AccessToken, tokenResponse.ExpiresIn);
        }, cancellationToken);
        return (clientId, token);
    }

    private async Task<List<JsonElement>?> SendIgdbRequestAsync(
        string endpoint,
        string query,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("IGDB request to {Endpoint}:\n{Query}", endpoint, query);

        using var response = await SendAuthorizedAsync(endpoint, query,
            HttpCompletionOption.ResponseContentRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogDebug("IGDB response from {Endpoint} ({Length} chars): {Body}",
            endpoint, body.Length, body.Length > 2000 ? body[..2000] + "..." : body);

        return JsonSerializer.Deserialize<List<JsonElement>>(body);
    }

    private EnrichmentResult MapToResult(EnrichmentContext context, JsonElement game)
    {
        string id = game.GetProperty("id").GetInt64().ToString();
        string? name = game.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        string? summary = game.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null;

        // Parse release date
        DateOnly? releaseDate = null;
        string? releaseDateYear = null;
        if (game.TryGetProperty("first_release_date", out var dateProp))
        {
            long timestamp = dateProp.GetInt64();
            var dateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
            releaseDate = DateOnly.FromDateTime(dateTime);
            releaseDateYear = dateTime.Year.ToString();
        }

        // Parse involved companies for developer/publisher
        string? developer = null;
        string? publisher = null;
        if (game.TryGetProperty("involved_companies", out var companiesProp))
        {
            foreach (var company in companiesProp.EnumerateArray())
            {
                string? companyName = company.TryGetProperty("company", out var c) &&
                                      c.TryGetProperty("name", out var n)
                    ? n.GetString()
                    : null;

                if (company.TryGetProperty("developer", out var devProp) && devProp.GetBoolean())
                {
                    developer ??= companyName;
                }

                if (company.TryGetProperty("publisher", out var pubProp) && pubProp.GetBoolean())
                {
                    publisher ??= companyName;
                }
            }
        }

        // Parse genres
        string? genre = null;
        if (game.TryGetProperty("genres", out var genresProp) && genresProp.GetArrayLength() > 0)
        {
            var firstGenre = genresProp[0];
            genre = firstGenre.TryGetProperty("name", out var gn) ? gn.GetString() : null;
        }

        // Parse player count from game modes
        int? players = null;
        if (game.TryGetProperty("game_modes", out var modesProp))
        {
            foreach (var mode in modesProp.EnumerateArray())
            {
                string? modeName = mode.TryGetProperty("name", out var mn) ? mn.GetString()?.ToLowerInvariant() : null;
                if (modeName == "multiplayer" || modeName == "co-operative")
                {
                    players = 2;
                    break;
                }
            }

            players ??= 1;
        }

        // Parse rating
        double? rating = null;
        if (game.TryGetProperty("total_rating", out var ratingProp))
        {
            rating = ratingProp.GetDouble();
        }

        var contentRatings = MapContentRatingClaims(game);

        // Parse media URLs
        var mediaUrls = new Dictionary<MediaType, string>();

        if (game.TryGetProperty("cover", out var coverProp) &&
            coverProp.TryGetProperty("url", out var coverUrlProp))
        {
            string? coverUrl = coverUrlProp.GetString();
            if (coverUrl != null)
            {
                mediaUrls[MediaType.Cover] = "https:" + coverUrl.Replace("t_thumb", "t_cover_big");
            }
        }

        if (game.TryGetProperty("screenshots", out var screenshotsProp) && screenshotsProp.GetArrayLength() > 0)
        {
            var firstScreenshot = screenshotsProp[0];
            if (firstScreenshot.TryGetProperty("url", out var ssUrl))
            {
                string? url = ssUrl.GetString();
                if (url != null)
                {
                    mediaUrls[MediaType.Screenshot] = "https:" + url.Replace("t_thumb", "t_screenshot_big");
                }
            }
        }

        if (game.TryGetProperty("artworks", out var artworksProp) && artworksProp.GetArrayLength() > 0)
        {
            var firstArtwork = artworksProp[0];
            if (firstArtwork.TryGetProperty("url", out var awUrl))
            {
                string? url = awUrl.GetString();
                if (url != null)
                {
                    mediaUrls[MediaType.Background] = "https:" + url.Replace("t_thumb", "t_1080p");
                }
            }
        }

        var data = new EnrichmentData
        {
            Description = summary,
            Publisher = publisher,
            Developer = developer,
            Genre = genre,
            ReleaseDate = releaseDate,
            Players = players,
            Rating = rating,
            ContentRatings = contentRatings
        };

        float confidence = _confidenceCalculator.Calculate(new MatchContext
        {
            SearchName = context.TitleName,
            ResultName = name,
            YearMatched = releaseDateYear != null && context.Year != null
                                                  && releaseDateYear.Length >= 4 && context.Year.Length >= 4
                                                  && releaseDateYear[..4] == context.Year[..4],
            HashMatched = false
        });

        _logger.LogInformation(
            "IGDB match for '{TitleName}': {IgdbName} (ID: {IgdbId}, Confidence: {Confidence:P0})",
            context.TitleName, name ?? "N/A", id, confidence);

        var artwork = new List<Romd.Admin.Application.Artwork.EnrichmentArtworkCandidate>();
        if (game.TryGetProperty("cover", out var cover))
            AddArtwork(cover, ArtworkRole.Poster, artwork);
        if (game.TryGetProperty("artworks", out var images) && images.ValueKind == JsonValueKind.Array)
            foreach (var image in images.EnumerateArray().Take(50))
            {
                AddArtwork(image, ArtworkRole.Hero, artwork);
                AddArtwork(image, ArtworkRole.Backdrop, artwork);
            }
        return EnrichmentResult.Found(id, confidence, data, mediaUrls) with { Artwork = artwork };
    }

    private static void AddArtwork(JsonElement image, ArtworkRole role,
        List<Romd.Admin.Application.Artwork.EnrichmentArtworkCandidate> candidates)
    {
        if (!image.TryGetProperty("url", out var url) || url.GetString() is not { } raw ||
            !image.TryGetProperty("width", out var width) || !width.TryGetInt32(out var w) ||
            !image.TryGetProperty("height", out var height) || !height.TryGetInt32(out var h))
            return;
        if (!Uri.TryCreate(raw.StartsWith("//", StringComparison.Ordinal) ? "https:" + raw : raw, UriKind.Absolute, out var uri))
            return;
        var assetId = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(assetId) || !assetId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return;
        candidates.Add(new(assetId, role, $"https://images.igdb.com/igdb/image/upload/t_original/{assetId}.jpg", w, h));
    }

    private IReadOnlyList<ContentRatingClaim> MapContentRatingClaims(JsonElement game)
    {
        if (!game.TryGetProperty("age_ratings", out var ageRatings) ||
            ageRatings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return ageRatings
            .EnumerateArray()
            .Select(MapContentRatingClaim)
            .OfType<ContentRatingClaim>()
            .ToList();
    }

    private ContentRatingClaim? MapContentRatingClaim(JsonElement ageRating)
    {
        string? organizationName = TryGetNestedString(ageRating, "organization", "name");
        if (!TryMapRatingBoard(organizationName, out var board))
        {
            _logger.LogWarning(
                "Dropping IGDB age rating {AgeRatingId}: unknown organization '{Organization}'",
                TryGetId(ageRating) ?? "unknown",
                organizationName ?? "<missing>");
            return null;
        }

        string? rawCode = TryGetNestedString(ageRating, "rating_category", "rating");
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            _logger.LogWarning(
                "Dropping IGDB age rating {AgeRatingId}: missing rating category for organization '{Organization}'",
                TryGetId(ageRating) ?? "unknown",
                organizationName);
            return null;
        }

        if (RatingBoardCatalog.TryResolve(board, rawCode) is null)
        {
            _logger.LogWarning(
                "Dropping IGDB age rating {AgeRatingId}: unrecognized {Board} rating category '{RatingCategory}'",
                TryGetId(ageRating) ?? "unknown",
                board,
                rawCode);
            return null;
        }

        return new ContentRatingClaim
        {
            Board = board,
            RawCode = rawCode,
            ExternalRatingId = TryGetId(ageRating),
            Descriptors = GetRatingContentDescriptions(ageRating),
            Synopsis = ageRating.TryGetProperty("synopsis", out var synopsis) &&
                       synopsis.ValueKind == JsonValueKind.String
                ? synopsis.GetString()
                : null
        };
    }

    private static IReadOnlyList<string> GetRatingContentDescriptions(JsonElement ageRating)
    {
        if (!ageRating.TryGetProperty("rating_content_descriptions", out var descriptions) ||
            descriptions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return descriptions
            .EnumerateArray()
            .Select(description => description.TryGetProperty("description", out var value) &&
                                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static bool TryMapRatingBoard(string? organizationName, out RatingBoard board)
    {
        string normalized = organizationName?
            .Trim()
            .ToUpperInvariant()
            .Replace("_", "")
            .Replace("-", "")
            .Replace(" ", "") ?? "";

        board = normalized switch
        {
            "ESRB" => RatingBoard.Esrb,
            "PEGI" => RatingBoard.Pegi,
            "CERO" => RatingBoard.Cero,
            "USK" => RatingBoard.Usk,
            "GRAC" => RatingBoard.Grac,
            "CLASSIND" => RatingBoard.ClassInd,
            "ACB" => RatingBoard.Acb,
            _ => default
        };

        return normalized is "ESRB" or "PEGI" or "CERO" or "USK" or "GRAC" or "CLASSIND" or "ACB";
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object &&
               property.TryGetProperty(nestedPropertyName, out var nested) &&
               nested.ValueKind == JsonValueKind.String
            ? nested.GetString()
            : null;
    }

    private static string? TryGetId(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var id))
            return null;

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetInt64().ToString(),
            JsonValueKind.String => id.GetString(),
            _ => null
        };
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record TwitchTokenResponse(
        [property: JsonPropertyName("access_token")]
        string AccessToken,
        [property: JsonPropertyName("expires_in")]
        int ExpiresIn,
        [property: JsonPropertyName("token_type")]
        string TokenType);

    internal sealed record MultiQueryResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("result")] List<JsonElement> Result);
}
