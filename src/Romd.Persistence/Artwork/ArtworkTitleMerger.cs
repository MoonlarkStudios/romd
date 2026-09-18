using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Artwork;

public sealed class ArtworkTitleMerger(RomdDbContext context)
{
    // Retain every distinct content revision. Target pin/pending intent wins; otherwise
    // carry the source's effective pin, never its pending request. Exact duplicates
    // keep the target identity, repairing incomplete target files from a ready source.
    public async Task StageAsync(int sourceTitleId, int targetTitleId, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Artwork merge requires a caller-owned transaction.");
        if (sourceTitleId == targetTitleId)
            throw new ArgumentException("Artwork merge requires distinct titles.", nameof(targetTitleId));

        // Artwork mutations take the title lock before changing assets or selections.
        // Stable ordering prevents opposing merges from taking locks in reverse order.
        foreach (var titleId in new[] { sourceTitleId, targetTitleId }.Order())
        {
            var count = await context.Titles.Where(title => title.Id == titleId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.RetainWithoutCatalog,
                    title => title.RetainWithoutCatalog), ct);
            if (count != 1)
                throw new InvalidOperationException("Both artwork merge titles must exist.");
        }
        await context.ArtworkSelections.Where(selection => selection.TitleId == sourceTitleId ||
                selection.TitleId == targetTitleId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(selection => selection.Revision,
                selection => selection.Revision), ct);
        var selections = await context.ArtworkSelections.AsNoTracking()
            .Where(selection => selection.TitleId == sourceTitleId || selection.TitleId == targetTitleId)
            .ToListAsync(ct);
        var assets = await context.ArtworkAssets.AsNoTracking()
            .Where(asset => asset.TitleId == sourceTitleId || asset.TitleId == targetTitleId).ToListAsync(ct);
        var targets = assets.Where(asset => asset.TitleId == targetTitleId)
            .ToDictionary(Key);
        var assetIds = new Dictionary<int, int>();

        // Composite title/role FK must be released before moving or deleting source assets.
        // Deletion also fences pending source jobs; no request identity transfers to the target.
        await context.ArtworkSelections.Where(selection => selection.TitleId == sourceTitleId).ExecuteDeleteAsync(ct);
        foreach (var asset in assets.Where(asset => asset.TitleId == sourceTitleId))
        {
            if (targets.TryGetValue(Key(asset), out var duplicate))
            {
                assetIds[asset.Id] = duplicate.Id;
                if (asset.IsEligible && !duplicate.IsEligible)
                {
                    var variants = await context.ArtworkVariants.AsNoTracking()
                        .Where(variant => variant.AssetId == asset.Id).ToListAsync(ct);
                    await context.ArtworkVariants.Where(variant => variant.AssetId == duplicate.Id)
                        .ExecuteDeleteAsync(ct);
                    foreach (var variant in variants)
                    {
                        variant.AssetId = duplicate.Id;
                        context.ArtworkVariants.Add(variant);
                    }
                    await context.ArtworkAssets.Where(candidate => candidate.Id == duplicate.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(candidate => candidate.OriginalFileId, asset.OriginalFileId)
                            .SetProperty(candidate => candidate.Width, asset.Width)
                            .SetProperty(candidate => candidate.Height, asset.Height)
                            .SetProperty(candidate => candidate.ContentType, asset.ContentType)
                            .SetProperty(candidate => candidate.IsEligible, true), ct);
                }
                await context.ArtworkAssets.Where(candidate => candidate.Id == asset.Id).ExecuteDeleteAsync(ct);
            }
            else
            {
                assetIds[asset.Id] = asset.Id;
                await context.ArtworkAssets.Where(candidate => candidate.Id == asset.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.TitleId, targetTitleId), ct);
            }
        }

        foreach (var source in selections.Where(selection => selection.TitleId == sourceTitleId &&
                     selection.Mode == ArtworkSelectionMode.Pinned && selection.PinnedAssetId.HasValue))
        {
            var target = selections.SingleOrDefault(selection => selection.TitleId == targetTitleId &&
                selection.Role == source.Role);
            if (target is { Mode: ArtworkSelectionMode.Pinned } || target?.PendingRequestId is not null)
                continue;
            var pinnedAssetId = assetIds[source.PinnedAssetId!.Value];
            if (target is null)
            {
                context.ArtworkSelections.Add(new ArtworkSelectionEntity
                {
                    TitleId = targetTitleId, Role = source.Role, Mode = ArtworkSelectionMode.Pinned,
                    PinnedAssetId = pinnedAssetId, Revision = checked(source.Revision + 1), FocalX = source.FocalX, FocalY = source.FocalY
                });
            }
            else
            {
                var revision = checked(target.Revision + 1);
                await context.ArtworkSelections.Where(selection => selection.TitleId == targetTitleId &&
                        selection.Role == source.Role)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(selection => selection.Mode, ArtworkSelectionMode.Pinned)
                        .SetProperty(selection => selection.PinnedAssetId, (int?)pinnedAssetId)
                        .SetProperty(selection => selection.FocalX, source.FocalX)
                        .SetProperty(selection => selection.FocalY, source.FocalY)
                        .SetProperty(selection => selection.Revision, revision), ct);
            }
        }
    }

    private static (ArtworkRole Role, string SourceId, string? ProviderAssetId, string ContentVersion)
        Key(ArtworkAssetEntity asset) => (asset.Role, asset.SourceId, asset.ProviderAssetId, asset.ContentVersion);
}
