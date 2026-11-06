using ErrorOr;
using System.Globalization;
using System.Text.Json;
using Romd.Admin.Application.MetadataProviders;

namespace Romd.Infrastructure.Enrichment;

public sealed partial class IgdbMetadataProvider
{
    internal async Task<ErrorOr<(string AppId, string Name)?>> GetSteamIdentityAsync(string gameId, CancellationToken ct)
    {
        if (!long.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return ProviderMatchErrors.InvalidInput;
        await InitializeAsync(ct);
        if (!IsConfigured)
            return ProviderMatchErrors.NotConfigured;
        try
        {
            await _rateLimiter.AcquireAsync(ProviderId, ct);
            var games = await SendIgdbRequestAsync("games",
                $"where id = {id}; fields id,name,external_games.uid,external_games.external_game_source.name; limit 1;", ct);
            if (games is not { Count: 1 })
                return ((string, string)?)null;
            var game = games[0];
            if (!game.TryGetProperty("id", out var returnedId) || returnedId.GetInt64() != id ||
                !game.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } gameName ||
                !game.TryGetProperty("external_games", out var external) || external.ValueKind != JsonValueKind.Array)
                return ((string, string)?)null;
            var ids = external.EnumerateArray().Where(x =>
                x.TryGetProperty("external_game_source", out var source) && source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("name", out var sourceName) && sourceName.GetString() == "Steam")
                .Select(x => x.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.String ? uid.GetString() : null)
                .Where(x => x is { Length: > 0 and <= 20 } && x.All(char.IsAsciiDigit) && x != "0")
                .Distinct().Take(2).ToArray();
            return ids.Length == 1 ? (ids[0]!, gameName) : ((string, string)?)null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or FormatException)
        { return ProviderMatchErrors.Unavailable; }
    }
}
