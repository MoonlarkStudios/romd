using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Providers;

public sealed record ArtworkMatchAuthorization(string ProviderId, string GameId, ArtworkRole Role, Guid Revision, bool IsProvisional = false);

public enum ArtworkMatchBlock { ProviderDisabled, ProviderUnconfigured, Unlinked, Failed, NeedsMatch }

public abstract record ArtworkMatchResolution
{
    private ArtworkMatchResolution() { }
    public sealed record Authorized(ArtworkMatchAuthorization Match) : ArtworkMatchResolution;
    public sealed record Blocked(ArtworkMatchBlock Reason) : ArtworkMatchResolution;
}

public interface IArtworkMatchAuthorizationStore
{
    Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct);
}

public interface IArtworkMatchAuthorizationService
{
    Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct);
    Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct);
    Task<bool> IsCurrentAsync(int titleId, ArtworkMatchAuthorization match, CancellationToken ct);
}

public sealed class ArtworkMatchAuthorizationService(IProviderIdentityService identities,
    IEnumerable<IProviderIdentityAdapter> providers, IArtworkMatchAuthorizationStore store) : IArtworkMatchAuthorizationService
{
    public Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct) =>
        identities.DiscoverAsync(titleId, context, ct);

    public async Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct)
    {
        var provider = providers.SingleOrDefault(x => x.Id == providerId);
        if (provider is null)
            return new ArtworkMatchResolution.Blocked(ArtworkMatchBlock.ProviderDisabled);
        var availability = await provider.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
            return new ArtworkMatchResolution.Blocked(ArtworkMatchBlock.ProviderDisabled);
        if (!availability.Configured)
            return new ArtworkMatchResolution.Blocked(ArtworkMatchBlock.ProviderUnconfigured);
        return await store.ReadAsync(titleId, providerId, role, ct);
    }

    public async Task<bool> IsCurrentAsync(int titleId, ArtworkMatchAuthorization match, CancellationToken ct) =>
        await store.ReadAsync(titleId, match.ProviderId, match.Role, ct) is ArtworkMatchResolution.Authorized current &&
        current.Match == match;
}
