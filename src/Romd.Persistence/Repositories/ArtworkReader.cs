using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Artwork;
using Romd.Domain.Catalog;
using Romd.Persistence.Artwork;

namespace Romd.Persistence.Repositories;

public sealed class ArtworkReader(RomdDbContext context) : IArtworkReader
{
    internal async Task<IReadOnlyDictionary<int, string?>> ResolvePosterUrlsAsync(
        IReadOnlyCollection<int> titleIds, CancellationToken ct)
    {
        var artwork = await ResolveAsync(titleIds, ct);
        return artwork.ToDictionary(entry => entry.Key,
            entry => entry.Value.FirstOrDefault(item => item.Role == ArtworkRole.Poster)?.ToContract().Url);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<ArtworkResolution>>> ResolveAsync(
        IReadOnlyCollection<int> titleIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<ArtworkResolution>>();
        if (titleIds.Count == 0)
            return result;
        // One SQL statement per bounded batch includes selections, assets, variants,
        // cover and preferences. READ COMMITTED callers therefore cannot combine a
        // newer pin with an older asset list, and ambient transactions remain owned
        // by the calling use case.
        var visibleAssets = context.ArtworkAssets.AvailableForPresentation();
        foreach (var ids in titleIds.Distinct().Chunk(100))
        {
            var snapshots = await context.Titles.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(t => new
                {
                    t.Id,
                    Assets = visibleAssets.Where(a => a.TitleId == t.Id && !a.IsRejected)
                        .Select(a => new { Asset = a, Variants = a.Variants.ToList() }).ToList(),
                    Selections = context.ArtworkSelections.Where(s => s.TitleId == t.Id).ToList(),
                    Cover = context.TitleMedia.Where(m => m.TitleId == t.Id && m.Type == "Cover")
                        .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Id).FirstOrDefault(),
                    Preferences = context.ArtworkPreferences.OrderBy(p => p.Priority).ToList()
                }).AsSingleQuery().ToListAsync(ct);
            foreach (var snapshot in snapshots)
            {
                var assets = snapshot.Assets.Select(a => (a.Asset.IsProvisional, Asset: new ArtworkAsset(a.Asset.Id,
                    a.Asset.TitleId, a.Asset.Role, a.Asset.SourceId, a.Asset.ProviderGameId,
                    a.Asset.ProviderAssetId,
                    new ArtworkVariant(a.Asset.OriginalFileId, a.Asset.ContentVersion,
                        a.Asset.ContentType, a.Asset.Width, a.Asset.Height, "original"),
                    a.Variants.Select(v => v.ToDomain()), true, a.Asset.CreatedAt,
                    a.Asset.Attribution, a.Asset.SourcePageUrl))).ToArray();
                var permanent = assets.Where(a => !a.IsProvisional).Select(a => a.Asset).ToArray();
                result[snapshot.Id] = Enum.GetValues<ArtworkRole>().Select(role =>
                {
                    var selection = snapshot.Selections.FirstOrDefault(s => s.Role == role)?.ToDomain()
                        ?? ArtworkSelection.CreateAutomatic(snapshot.Id, role);
                    var preferences = snapshot.Preferences.Where(p => p.Role == role).Select(p => p.SourceId).ToArray();
                    var resolved = ArtworkResolver.Resolve(selection, permanent, preferences, snapshot.Cover?.ToDomain());
                    return selection is { Mode: ArtworkSelectionMode.Automatic, PendingRequestId: null } &&
                        resolved.Asset is null && resolved.LegacyCover is null
                        ? ArtworkResolver.Resolve(selection, assets.Where(a => a.IsProvisional).Select(a => a.Asset), preferences)
                        : resolved;
                }).ToArray();
            }
        }
        return result;
    }

    public Task<ArtworkFileReference?> FindVariantAsync(int assetId, string name, string contentVersion,
        CancellationToken ct = default)
    {
        var visibleAssets = context.ArtworkAssets.AvailableForPresentation();
        return (from variant in context.ArtworkVariants.AsNoTracking()
                join asset in visibleAssets on variant.AssetId equals asset.Id
                join file in context.Files on variant.FileId equals file.Id
                where variant.AssetId == assetId && variant.Name == name && variant.ContentVersion == contentVersion
                select new ArtworkFileReference(file.Sha256, variant.ContentType, asset.IsProvisional,
                    asset.ProviderGameId != null || asset.ProviderAssetId != null))
            .SingleOrDefaultAsync(ct);
    }
}
