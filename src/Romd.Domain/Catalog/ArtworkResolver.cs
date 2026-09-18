namespace Romd.Domain.Catalog;

public sealed record ArtworkResolution(
    ArtworkRole Role,
    ArtworkAsset? Asset,
    TitleMedia? LegacyCover,
    ArtworkFit Fit,
    ArtworkFallbackReason FallbackReason, int FocalX = 50, int FocalY = 50);

/// <summary>
/// Resolves only retained content. Within a source the oldest retained candidate
/// wins, then the asset ID. Unlisted sources follow preferred sources in ordinal
/// order, so disabling a provider never hides its downloaded artwork.
/// </summary>
public static class ArtworkResolver
{
    public static ArtworkResolution Resolve(
        ArtworkSelection selection,
        IEnumerable<ArtworkAsset> assets,
        IReadOnlyList<string> providerPreference,
        TitleMedia? legacyCover = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(providerPreference);
        var eligible = assets.Where(x => x.TitleId == selection.TitleId &&
            x.Role == selection.Role && x.IsEligible).ToArray();
        var selected = selection.Mode == ArtworkSelectionMode.Pinned
            ? eligible.FirstOrDefault(x => x.Id == selection.PinnedAssetId)
            : null;
        selected ??= eligible.OrderBy(x => x.SourceId == "user" ? 0 : 1)
            .ThenBy(x => ProviderRank(x.SourceId, providerPreference))
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefault();
        if (selected is not null)
        {
            var usesPin = selection.Mode == ArtworkSelectionMode.Pinned && selected.Id == selection.PinnedAssetId;
            return new ArtworkResolution(selection.Role, selected, null,
                selection.Role switch
                {
                    ArtworkRole.Logo => ArtworkFit.Contain,
                    ArtworkRole.Poster when !usesPin => ArtworkFit.Contain,
                    _ => ArtworkFit.Cover
                }, ArtworkFallbackReason.None,
                usesPin ? selection.FocalX : 50, usesPin ? selection.FocalY : 50);
        }
        if (selection.Role == ArtworkRole.Poster && legacyCover is { Type: MediaType.Cover } &&
            legacyCover.TitleId == selection.TitleId)
            return new ArtworkResolution(selection.Role, null, legacyCover, ArtworkFit.Contain, ArtworkFallbackReason.LegacyCover);
        return new ArtworkResolution(selection.Role, null, null, ArtworkFit.Contain, ArtworkFallbackReason.NoArtwork);
    }

    private static int ProviderRank(string sourceId, IReadOnlyList<string> preference)
    {
        for (var i = 0; i < preference.Count; i++)
            if (string.Equals(preference[i], sourceId, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }
}
