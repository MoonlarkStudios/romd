using ErrorOr;
using System.Text.Json;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Infrastructure.Enrichment;

public sealed partial class IgdbMetadataProvider : IProviderIdentityAdapter
{
    string IProviderIdentityAdapter.Id => ProviderId;
    string IProviderIdentityAdapter.Name => DisplayName;
    IReadOnlyList<string> IProviderIdentityAdapter.Capabilities => ["metadata", "artwork", "search", "resolve"];

    async Task<ProviderAvailability> IProviderIdentityAdapter.GetAvailabilityAsync(CancellationToken ct)
    {
        if (_settingsService is not null)
        {
            var settings = await _settingsService.GetAsync(ct);
            return new(settings.Enabled, settings.IsConfigured);
        }
        return new(true, IsConfigured);
    }

    async Task<ErrorOr<ProviderGameSearchResultDto>> IProviderIdentityAdapter.SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl))
            return ProviderMatchErrors.InvalidQuery;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await InitializeAsync(deadline.Token);
            if (!IsConfigured)
                return ProviderMatchErrors.NotConfigured;
            var games = new Dictionary<string, ProviderGameDto>(StringComparer.Ordinal);
            var complete = true;
            for (var page = 0; page < 5; page++)
            {
                var result = await QueryIdentityAsync($"search \"{EscapeQuery(query.Trim())}\"; offset {page * 100};", 100, deadline.Token);
                if (result.IsError)
                    return result.Errors;
                foreach (var game in result.Value)
                    // Live relevance may move a game between responses. A repeated identity cannot prove exhaustion.
                    complete &= games.TryAdd(game.Id, game);
                if (result.Value.Count < 100)
                    return new ProviderGameSearchResultDto(games.Values.ToArray(), complete
                        ? ProviderGameSearchCompleteness.Complete : ProviderGameSearchCompleteness.ProviderLimited, 500);
            }
            return new ProviderGameSearchResultDto(games.Values.ToArray(), ProviderGameSearchCompleteness.BudgetLimited, 500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProviderMatchErrors.Unavailable; }
        catch (Exception e) when (e is HttpRequestException or IOException) { return ProviderMatchErrors.Unavailable; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { return ProviderMatchErrors.InvalidResponse; }
    }

    async Task<ErrorOr<ProviderGameDto>> IProviderIdentityAdapter.ResolveAsync(string idOrUrl, CancellationToken ct)
    {
        var key = ProviderIdentityInput.Parse(idOrUrl, "igdb.com", "/games/", allowSlug: true);
        if (key is null)
            return ProviderMatchErrors.InvalidInput;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await InitializeAsync(deadline.Token);
            if (!IsConfigured)
                return ProviderMatchErrors.NotConfigured;
            var numeric = key.All(char.IsAsciiDigit);
            var query = numeric ? $"where id = {key};" : $"where slug = \"{key}\";";
            var result = await QueryIdentityAsync(query, 2, deadline.Token);
            if (result.IsError)
                return result.Errors;
            if (result.Value.Count == 0)
                return ProviderMatchErrors.NotFound;
            if (result.Value.Count != 1 || (numeric ? result.Value[0].Id != key :
                !result.Value[0].Url.EndsWith("/" + key, StringComparison.Ordinal)))
                return ProviderMatchErrors.InvalidResponse;
            return result.Value[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProviderMatchErrors.Unavailable; }
        catch (Exception e) when (e is HttpRequestException or IOException) { return ProviderMatchErrors.Unavailable; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { return ProviderMatchErrors.InvalidResponse; }
    }

    private async Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> QueryIdentityAsync(string query, int limit, CancellationToken ct)
    {
        await _rateLimiter.AcquireAsync(ProviderId, ct);
        var response = await ReadArtworkResponseAsync(query +
            $" fields id,name,slug,cover.image_id,first_release_date,platforms.id,platforms.name,alternative_names.name,release_dates.y,release_dates.platform.id,release_dates.platform.name; limit {limit};", ct);
        if (response.IsError)
            return response.Errors.Select(error => error.Code switch
            {
                "Artwork.ProviderInvalidResponse" => ProviderMatchErrors.InvalidResponse,
                "Artwork.ProviderUnavailable" => ProviderMatchErrors.Unavailable,
                _ => error
            }).ToList();
        using var document = response.Value;
        var rows = document.RootElement;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > limit)
            return ProviderMatchErrors.InvalidResponse;
        var platformMap = await GetPlatformMapAsync(ct);
        var games = new List<ProviderGameDto>();
        foreach (var row in rows.EnumerateArray())
        {
            if (IdentityPreview(row, platformMap) is not { } game)
                return ProviderMatchErrors.InvalidResponse;
            games.Add(game);
        }
        if (games.Select(game => game.Id).Distinct(StringComparer.Ordinal).Count() != games.Count)
            return ProviderMatchErrors.InvalidResponse;
        return ErrorOrFactory.From<IReadOnlyList<ProviderGameDto>>(games);
    }

    private static ProviderGameDto? IdentityPreview(JsonElement game, IReadOnlyDictionary<string, int> platformMap)
    {
        if (game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0 ||
            !game.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 and <= 500 } title ||
            !game.TryGetProperty("slug", out var slug) || slug.GetString() is not { Length: > 0 and <= 200 } path ||
            string.IsNullOrWhiteSpace(title) || title.Any(char.IsControl) ||
            !path.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return null;
        string? thumbnail = null;
        if (game.TryGetProperty("cover", out var cover) && cover.ValueKind != JsonValueKind.Null)
        {
            if (!cover.TryGetProperty("image_id", out var image) || image.GetString() is not { Length: > 0 and <= 100 } imageId ||
                !imageId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return null;
            thumbnail = $"https://images.igdb.com/igdb/image/upload/t_cover_small/{imageId}.jpg";
        }
        int? year = null;
        if (game.TryGetProperty("first_release_date", out var date) && date.ValueKind != JsonValueKind.Null)
        {
            if (!date.TryGetInt64(out var unix))
                return null;
            year = DateTimeOffset.FromUnixTimeSeconds(unix).Year;
        }
        var platforms = Names(game, "platforms").ToList();
        if (game.TryGetProperty("platforms", out var platformRows) && platformRows.ValueKind == JsonValueKind.Array)
            foreach (var platform in platformRows.EnumerateArray())
                platforms.AddRange(MappedPlatformKeys(platform, platformMap));
        var aliases = Names(game, "alternative_names");
        var releases = new List<ProviderReleaseDto>();
        if (game.TryGetProperty("release_dates", out var releaseRows) && releaseRows.ValueKind != JsonValueKind.Null)
        {
            if (releaseRows.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var release in releaseRows.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                    return null;
                int? releaseYear = null;
                if (release.TryGetProperty("y", out var y) && y.ValueKind != JsonValueKind.Null)
                {
                    if (!y.TryGetInt32(out var knownYear) || knownYear is < 1 or > 9999)
                        return null;
                    releaseYear = knownYear;
                }
                string? platform = null;
                if (release.TryGetProperty("platform", out var p) && p.ValueKind != JsonValueKind.Null)
                    platform = Name(p);
                // Keep provider display evidence and add mapped local names for platform-scoped years.
                releases.Add(new(releaseYear, platform));
                if (release.TryGetProperty("platform", out var releasePlatform) && releasePlatform.ValueKind != JsonValueKind.Null)
                    foreach (var key in MappedPlatformKeys(releasePlatform, platformMap))
                        releases.Add(new(releaseYear, key));
            }
        }
        return new(number.ToString(System.Globalization.CultureInfo.InvariantCulture), title,
            $"https://www.igdb.com/games/{path}", thumbnail, year, platforms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), aliases, releases.Distinct().ToArray());
    }

    // Provider display labels can differ from the local name (e.g. Mega Drive/Genesis).
    // Only explicit numeric mappings corroborate identity; never split or fuzzy-match names.
    private static IEnumerable<string> MappedPlatformKeys(JsonElement platform, IReadOnlyDictionary<string, int> platformMap)
    {
        if (!platform.TryGetProperty("id", out var id))
            return [];
        if (!id.TryGetInt32(out var value) || value <= 0)
            throw new JsonException("Invalid provider platform ID.");
        return platformMap.Where(mapping => mapping.Value == value).Select(mapping => mapping.Key);
    }

    private static string[] Names(JsonElement game, string property)
    {
        if (!game.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null)
            return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid provider identity names.");
        return values.EnumerateArray().Select(Name).ToArray();
    }

    private static string Name(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) ||
            name.GetString() is not { Length: > 0 and <= 500 } text || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            throw new JsonException("Invalid provider identity name.");
        return text;
    }
}
