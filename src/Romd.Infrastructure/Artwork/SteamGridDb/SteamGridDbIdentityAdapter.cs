using ErrorOr;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Infrastructure.Artwork.SteamGridDb;

public sealed class SteamGridDbIdentityAdapter(SteamGridDbArtworkProvider provider, ISteamGridDbSettingsService settings)
    : IProviderIdentityAdapter
{
    public string Id => "steamgriddb";
    public string Name => "SteamGridDB";
    public IReadOnlyList<string> Capabilities => ["artwork", "search", "resolve"];
    public async Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        var state = await settings.GetAsync(ct);
        return new(state.Enabled, state.IsConfigured);
    }
    public async Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> SearchAsync(string query, CancellationToken ct)
    {
        var result = await provider.SearchAsync(query, ct);
        if (result.IsError) return result.Errors;
        return result.Value.Select(x => new ProviderGameDto(x.Id, x.Name, $"https://www.steamgriddb.com/game/{x.Id}")).ToArray();
    }
    public Task<ErrorOr<ProviderGameDto>> ResolveAsync(string idOrUrl, CancellationToken ct) => provider.ResolveIdentityAsync(idOrUrl, ct);
}
