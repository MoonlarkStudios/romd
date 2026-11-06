using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Artwork;
using Romd.Domain.Catalog;

namespace Romd.Application.Common.Artwork;

public static class ArtworkContractMapping
{
    public static ResolvedArtworkDto ToContract(this ArtworkResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Asset is { } asset)
        {
            var assetId = IdCoder.Encode(asset.Id);
            var variants = asset.Variants
                .OrderByDescending(variant => (long)variant.Width * variant.Height)
                .ThenBy(variant => variant.Name, StringComparer.Ordinal)
                .Select(variant => new ArtworkVariantDto(
                    variant.Name,
                    $"/artwork/{assetId}/{Uri.EscapeDataString(variant.Name)}/{Uri.EscapeDataString(variant.ContentVersion)}",
                    variant.ContentVersion,
                    variant.ContentType,
                    variant.Width,
                    variant.Height))
                .ToArray();
            var preferred = variants.FirstOrDefault();
            return new ResolvedArtworkDto(
                resolution.Role.ToString(), assetId, preferred?.ContentVersion, preferred?.Url,
                preferred?.Width, preferred?.Height, asset.Original.Width, asset.Original.Height,
                resolution.Fit.ToString(), resolution.FallbackReason.ToString(), variants, resolution.FocalX, resolution.FocalY);
        }

        var mediaId = resolution.LegacyCover is { } cover ? IdCoder.Encode(cover.Id) : null;
        return new ResolvedArtworkDto(
            resolution.Role.ToString(), null, mediaId is null ? null : $"media-{mediaId}",
            mediaId is null ? null : $"/media/{mediaId}", null, null, null, null,
            resolution.Fit.ToString(), resolution.FallbackReason.ToString(), []);
    }
}
