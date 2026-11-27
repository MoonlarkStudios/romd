using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Providers;

public sealed record ArtworkMatchAuthorization(ProviderMatchAuthorization Match, ArtworkRole Role)
{
    public string ProviderId => Match.ProviderId;
    public string GameId => Match.GameId;
    public Guid Revision => Match.Revision;
    public bool IsProvisional => Match.IsProvisional;
}

public abstract record ArtworkMatchResolution
{
    private ArtworkMatchResolution() { }
    public sealed record Authorized(ArtworkMatchAuthorization Match) : ArtworkMatchResolution;
    public sealed record Blocked(ProviderMatchBlock Reason) : ArtworkMatchResolution;
}

public interface IArtworkMatchAuthorizationService
{
    Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct);
    Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct);
    Task<bool> IsCurrentAsync(int titleId, ArtworkMatchAuthorization match, CancellationToken ct);
}

public sealed class ArtworkMatchAuthorizationService(IProviderIdentityService identities,
    IEnumerable<IProviderIdentityAdapter> providers, IProviderMatchAuthorizationStore store) : IArtworkMatchAuthorizationService
{
    public Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct) =>
        identities.DiscoverArtworkAsync(titleId, context, ct);

    public async Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct)
    {
        var provider = providers.SingleOrDefault(x => x.Id == providerId);
        if (provider is null)
            return new ArtworkMatchResolution.Blocked(ProviderMatchBlock.ProviderDisabled);
        var availability = await provider.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
            return new ArtworkMatchResolution.Blocked(ProviderMatchBlock.ProviderDisabled);
        if (!availability.Configured)
            return new ArtworkMatchResolution.Blocked(ProviderMatchBlock.ProviderUnconfigured);
        return await store.ReadAuthorizationAsync(titleId, providerId, ct) switch
        {
            ProviderMatchAuthorizationResult.Authorized(var match) => new ArtworkMatchResolution.Authorized(new(match, role)),
            ProviderMatchAuthorizationResult.Blocked(var reason) => new ArtworkMatchResolution.Blocked(reason),
            _ => null
        };
    }

    public async Task<bool> IsCurrentAsync(int titleId, ArtworkMatchAuthorization match, CancellationToken ct) =>
        await store.ReadAuthorizationAsync(titleId, match.ProviderId, ct) is ProviderMatchAuthorizationResult.Authorized(var current) &&
        current == match.Match;
}
