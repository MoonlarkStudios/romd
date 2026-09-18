using ErrorOr;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Artwork;

public sealed class ArtworkAssetSource(IEnumerable<IArtworkProvider> providers) : IArtworkAssetSource
{
    private readonly IReadOnlyDictionary<string, IArtworkProvider> _providers =
        providers.ToDictionary(provider => provider.Capabilities.ProviderId, StringComparer.Ordinal);

    public Task<ErrorOr<DownloadedArtworkAsset>> DownloadAsync(string providerId, string providerGameId,
        string providerAssetId, ArtworkRole role, string trustedAssetUrl, CancellationToken ct = default) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider.DownloadAsync(providerId, providerGameId, providerAssetId, role, trustedAssetUrl, ct)
            : Task.FromResult<ErrorOr<DownloadedArtworkAsset>>(ArtworkProviderErrors.InvalidRequest());

    public Task<ErrorOr<DownloadedArtworkAsset>> DownloadPreviewAsync(string providerId, ArtworkRole role,
        string trustedPreviewUrl, CancellationToken ct = default) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider.DownloadPreviewAsync(providerId, role, trustedPreviewUrl, ct)
            : Task.FromResult<ErrorOr<DownloadedArtworkAsset>>(ArtworkProviderErrors.InvalidRequest());
}
