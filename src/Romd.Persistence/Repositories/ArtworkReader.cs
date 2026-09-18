using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Artwork;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Repositories;

public sealed class ArtworkReader(RomdDbContext context) : IArtworkReader
{
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<ArtworkResolution>>> ResolveAsync(
        IReadOnlyCollection<int> titleIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<ArtworkResolution>>();
        if (titleIds.Count == 0) return result;
        // One SQL statement per bounded batch includes selections, assets, variants,
        // cover and preferences. READ COMMITTED callers therefore cannot combine a
        // newer pin with an older asset list, and ambient transactions remain owned
        // by the calling use case.
        foreach (var ids in titleIds.Distinct().Chunk(100))
        {
            var snapshots = await context.Titles.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(t => new
                {
                    t.Id,
                    Assets = context.ArtworkAssets.Where(a => a.TitleId == t.Id)
                        .Select(a => new { Asset = a, Variants = a.Variants.ToList() }).ToList(),
                    Selections = context.ArtworkSelections.Where(s => s.TitleId == t.Id).ToList(),
                    Cover = context.TitleMedia.Where(m => m.TitleId == t.Id && m.Type == "Cover")
                        .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Id).FirstOrDefault(),
                    Preferences = context.ArtworkPreferences.OrderBy(p => p.Priority).ToList()
                }).AsSingleQuery().ToListAsync(ct);
            foreach (var snapshot in snapshots)
            {
                var assets = snapshot.Assets.Select(a => new ArtworkAsset(a.Asset.Id,
                    a.Asset.TitleId, a.Asset.Role, a.Asset.SourceId, a.Asset.ProviderGameId,
                    a.Asset.ProviderAssetId,
                    new ArtworkVariant(a.Asset.OriginalFileId, a.Asset.ContentVersion,
                        a.Asset.ContentType, a.Asset.Width, a.Asset.Height, "original"),
                    a.Variants.Select(v => v.ToDomain()), a.Asset.IsEligible, a.Asset.CreatedAt,
                    a.Asset.Attribution, a.Asset.SourcePageUrl)).ToArray();
                result[snapshot.Id] = Enum.GetValues<ArtworkRole>().Select(role => ArtworkResolver.Resolve(
                    snapshot.Selections.FirstOrDefault(s => s.Role == role)?.ToDomain()
                        ?? ArtworkSelection.CreateAutomatic(snapshot.Id, role),
                    assets,
                    snapshot.Preferences.Where(p => p.Role == role).Select(p => p.SourceId).ToArray(),
                    snapshot.Cover?.ToDomain())).ToArray();
            }
        }
        return result;
    }

    public Task<ArtworkFileReference?> FindVariantAsync(int assetId, string name, string contentVersion,
        CancellationToken ct = default) =>
        context.ArtworkVariants.AsNoTracking()
            .Where(v => v.AssetId == assetId && v.Name == name && v.ContentVersion == contentVersion &&
                context.ArtworkAssets.Any(a => a.Id == v.AssetId && a.IsEligible))
            .Join(context.Files, v => v.FileId, f => f.Id,
                (v, f) => new ArtworkFileReference(f.Sha256, v.ContentType))
            .SingleOrDefaultAsync(ct);
}
