using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

public sealed partial class TitleProviderMatchStore
{
    private async Task<TitleMetadataPayload?> ReadPromotableMetadataAsync(int titleId, string providerId, string gameId,
        Guid revision, CancellationToken ct)
    {
        var json = await _db.Set<TitleProvisionalMetadataEntity>().Where(value => value.TitleId == titleId &&
            value.ProviderId == providerId && value.ProviderGameId == gameId && value.MatchRevision == revision &&
            ProviderIdentityDatabaseFunctions.ProvisionalMetadataGame(value.TitleId, value.ProviderId,
                ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.MetadataVersion) == gameId)
            .Select(value => value.MetadataJson).SingleOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<TitleMetadataPayload>(json);
    }

    private async Task<bool> WithdrawContributionsAsync(int titleId, string providerId, string? selectedGameId, CancellationToken ct)
    {
        await _db.Set<TitleProvisionalMetadataEntity>().Where(value => value.TitleId == titleId && value.ProviderId == providerId)
            .ExecuteDeleteAsync(ct);
        var providerLayers = await _db.TitleMetadataLayers
            .Where(layer => layer.TitleId == titleId && layer.SourceType == (int)MetadataSourceType.Provider &&
                layer.SourceId.ToLower() == providerId)
            .ExecuteDeleteAsync(ct);
        // Older automatically acquired media records the source but has no provider game ID.
        // Explicit gallery copies have a separate source key and can share the same stored file.
        var providerMedia = await _db.TitleMedia
            .Where(media => media.TitleId == titleId && media.SourceId.ToLower() == providerId)
            .ExecuteDeleteAsync(ct);

        var selections = _db.ArtworkSelections.Where(selection => selection.TitleId == titleId);
        await _db.ArtworkAssets.Where(asset => asset.TitleId == titleId && asset.SourceId == providerId &&
            !asset.IsRejected && (selectedGameId == null || asset.ProviderGameId != selectedGameId) &&
            !selections.Any(selection => selection.PinnedAssetId == asset.Id))
            .ExecuteDeleteAsync(ct);
        await _db.Set<ArtworkAcquisitionEntity>().Where(outcome => outcome.TitleId == titleId && outcome.SourceId == providerId)
            .ExecuteDeleteAsync(ct);
        return providerLayers > 0 || providerMedia > 0;
    }
}
