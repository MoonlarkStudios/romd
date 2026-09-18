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

    async Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> IProviderIdentityAdapter.SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) return ProviderMatchErrors.InvalidInput;
        return await QueryIdentityAsync($"search \"{EscapeQuery(query.Trim())}\";", ct);
    }

    async Task<ErrorOr<ProviderGameDto>> IProviderIdentityAdapter.ResolveAsync(string idOrUrl, CancellationToken ct)
    {
        var key = ProviderIdentityInput.Parse(idOrUrl, "igdb.com", "/games/", allowSlug: true);
        if (key is null) return ProviderMatchErrors.InvalidInput;
        var query = key.All(char.IsAsciiDigit) ? $"where id = {key};" : $"where slug = \"{key}\";";
        var result = await QueryIdentityAsync(query, ct);
        if (result.IsError) return result.Errors;
        return result.Value.Count == 1 ? result.Value[0] : ProviderMatchErrors.NotFound;
    }

    private async Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> QueryIdentityAsync(string query, CancellationToken ct)
    {
        await InitializeAsync(ct);
        if (!IsConfigured) return ProviderMatchErrors.Unavailable;
        try
        {
            await _rateLimiter.AcquireAsync(ProviderId, ct);
            await EnsureAccessTokenAsync(ct);
            var games = await SendIgdbRequestAsync("games",
                query + " fields id,name,slug,cover.image_id,first_release_date,platforms.name; limit 20;", ct);
            return (games ?? []).Select(IdentityPreview).Where(x => x is not null).Cast<ProviderGameDto>().ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or ArgumentOutOfRangeException)
        {
            return ProviderMatchErrors.Unavailable;
        }
    }

    private static ProviderGameDto? IdentityPreview(JsonElement game)
    {
        if (!game.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0 ||
            !game.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } title ||
            !game.TryGetProperty("slug", out var slug) || slug.GetString() is not { Length: > 0 } path ||
            !path.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        string? thumbnail = null;
        if (game.TryGetProperty("cover", out var cover) && cover.TryGetProperty("image_id", out var image) &&
            image.GetString() is { Length: > 0 } imageId && imageId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            thumbnail = $"https://images.igdb.com/igdb/image/upload/t_cover_small/{imageId}.jpg";
        int? year = game.TryGetProperty("first_release_date", out var date) && date.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).Year : null;
        var platforms = game.TryGetProperty("platforms", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToArray() : [];
        return new(number.ToString(System.Globalization.CultureInfo.InvariantCulture), title,
            $"https://www.igdb.com/games/{path}", thumbnail, year, platforms);
    }
}
