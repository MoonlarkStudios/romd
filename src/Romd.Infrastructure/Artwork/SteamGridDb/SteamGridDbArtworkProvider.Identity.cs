using ErrorOr;
using System.Text.Json;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Infrastructure.Enrichment;

namespace Romd.Infrastructure.Artwork.SteamGridDb;

public sealed partial class SteamGridDbArtworkProvider
{
    public async Task<ErrorOr<ProviderGameDto>> ResolveIdentityAsync(string input, CancellationToken ct)
    {
        var id = ProviderIdentityInput.Parse(input, "steamgriddb.com", "/game/");
        if (id is null) return ProviderMatchErrors.InvalidInput;
        var result = await ApiAsync($"games/id/{id}", ct);
        if (result.IsError) return result.Errors;
        using var json = result.Value;
        try
        {
            var game = json.RootElement.GetProperty("data");
            if (Id(game) != id || game.GetProperty("name").GetString() is not { Length: > 0 } name)
                return ProviderMatchErrors.NotFound;
            return new ProviderGameDto(id, name, $"https://www.steamgriddb.com/game/{id}");
        }
        catch (Exception e) when (IsInvalidJson(e)) { return ProviderMatchErrors.NotFound; }
    }
}
