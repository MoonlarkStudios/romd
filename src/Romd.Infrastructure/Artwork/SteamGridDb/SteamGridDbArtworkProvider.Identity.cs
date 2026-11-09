using ErrorOr;
using System.Text.Json;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Infrastructure.Enrichment;

namespace Romd.Infrastructure.Artwork.SteamGridDb;

public sealed partial class SteamGridDbArtworkProvider
{
    private static int? ReleaseYear(JsonElement game)
    {
        if (!game.TryGetProperty("release_date", out var date) || date.ValueKind == JsonValueKind.Null)
            return null;
        if (date.ValueKind != JsonValueKind.Number || !date.TryGetInt64(out var unix))
            throw new JsonException("Invalid provider release date.");
        try
        { return DateTimeOffset.FromUnixTimeSeconds(unix).Year; }
        catch (ArgumentOutOfRangeException exception) { throw new JsonException("Invalid provider release date.", exception); }
    }

    internal async Task<ErrorOr<ProviderGame>> ResolveSteamIdentityAsync(string appId, CancellationToken ct)
    {
        if (appId.Length is 0 or > 20 || !appId.All(char.IsAsciiDigit) || appId == "0")
            return ProviderMatchErrors.InvalidInput;
        var result = await ApiAsync($"games/steam/{appId}", ct);
        if (result.IsError)
            return result.Errors;
        using var json = result.Value;
        try
        {
            var game = json.RootElement.GetProperty("data");
            var id = Id(game);
            if (!ValidIdentityName(game.GetProperty("name").GetString(), out var name))
                return ArtworkProviderErrors.InvalidResponse();
            return new ProviderGame(id, name, $"https://www.steamgriddb.com/game/{id}", Year: ReleaseYear(game));
        }
        catch (Exception e) when (IsInvalidJson(e)) { return ArtworkProviderErrors.InvalidResponse(); }
    }

    /// <summary>
    ///     Resolves one game by its id or page URL. A game the provider does not have is not found; an answer of
    ///     another shape, or for another game, is an invalid provider response.
    /// </summary>
    public async Task<ErrorOr<ProviderGame>> ResolveIdentityAsync(string input, CancellationToken ct)
    {
        var id = ProviderIdentityInput.Parse(input, "steamgriddb.com", "/game/");
        if (id is null)
            return ProviderMatchErrors.InvalidInput;
        var result = await ApiAsync($"games/id/{id}", ct);
        if (result.IsError)
            return result.Errors;
        using var json = result.Value;
        try
        {
            var game = json.RootElement.GetProperty("data");
            if (Id(game) != id || !ValidIdentityName(game.GetProperty("name").GetString(), out var name))
                return ArtworkProviderErrors.InvalidResponse();
            return new ProviderGame(id, name, $"https://www.steamgriddb.com/game/{id}", Year: ReleaseYear(game));
        }
        catch (Exception e) when (IsInvalidJson(e)) { return ArtworkProviderErrors.InvalidResponse(); }
    }

    private static bool ValidIdentityName(string? value, out string name)
    {
        name = value ?? string.Empty;
        return !string.IsNullOrWhiteSpace(name) && name.Length <= 500 && !name.Any(char.IsControl);
    }
}
