using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;

namespace Romd.Persistence.Artwork;

internal static class ArtworkPresentationAssets
{
    internal static Task<int> PromoteProvisionalAsync(this IQueryable<ArtworkAssetEntity> assets, int titleId,
        string providerId, string gameId, CancellationToken ct) =>
        assets.Where(asset => asset.TitleId == titleId && asset.SourceId == providerId && asset.ProviderGameId == gameId &&
            asset.IsProvisional && !asset.IsRejected)
            .ExecuteUpdateAsync(set => set.SetProperty(asset => asset.IsProvisional, false)
                .SetProperty(asset => asset.IsEligible, true), ct);

    internal static IQueryable<ArtworkAssetEntity> AvailableForPresentation(this IQueryable<ArtworkAssetEntity> assets) =>
        assets.Where(asset => asset.IsEligible || asset.IsProvisional && !asset.IsRejected &&
            asset.ProviderGameId != null &&
            (asset.SourceId == "igdb"
                ? ProviderIdentityDatabaseFunctions.ProvisionalMetadataGame(asset.TitleId, asset.SourceId,
                    ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.MetadataVersion)
                : ProviderIdentityDatabaseFunctions.ProvisionalGame(asset.TitleId, asset.SourceId,
                    ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.Version)) == asset.ProviderGameId);
}
