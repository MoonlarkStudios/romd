using ErrorOr;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

/// <summary>Collects a validated provider original without changing artwork selections.</summary>
public sealed class GalleryArtworkService(IArtworkBrowsingService browsing, IArtworkAssetSource source,
    IArtworkImageProcessor processor, IFileStorageService storage, ITitleRepository titles,
    IUnitOfWork unitOfWork, IAdminEventOutbox outbox)
{
    public async Task<ErrorOr<TitleMediaRef>> ImportAsync(int titleId, Guid actor, string reference, CancellationToken ct)
    {
        var validation = await browsing.ValidateCandidateAsync(titleId, actor, reference, ct);
        if (validation.IsError) return validation.Errors;
        if (await titles.GetWithCollectionsAsync(titleId, ct) is null) return CatalogErrors.TitleNotFound(titleId);
        var candidate = validation.Value;
        var downloaded = await source.DownloadAsync(candidate.ProviderId, candidate.GameId, candidate.AssetId,
            candidate.Role, candidate.TrustedAssetUrl, ct);
        if (downloaded.IsError) return downloaded.Errors;
        var image = await processor.ProcessAsync(downloaded.Value.Bytes, candidate.Role, ct);
        if (image.IsError) return image.Errors;
        await using var content = new MemoryStream(downloaded.Value.Bytes, false);
        var stored = await storage.StoreAsync(content, ct: ct);
        var type = candidate.MediaType ?? candidate.Role switch
        {
            ArtworkRole.Poster => MediaType.Cover,
            ArtworkRole.Hero or ArtworkRole.Backdrop => MediaType.Background,
            ArtworkRole.Logo => MediaType.Logo,
            _ => throw new ArgumentOutOfRangeException(nameof(candidate.Role))
        };
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var original = TitleMedia.CreateNew(titleId, type, stored.File.Id, $"gallery:{candidate.ProviderId}",
            image.Value.ContentType, candidate.TrustedAssetUrl);
        original.SetAttribution(candidate.Attribution, candidate.SourcePageUrl);
        await titles.AppendGalleryMediaStagedAsync(original, ct);
        // The download may have outlived a provider match change. Roll back the append in that case.
        var current = await browsing.ValidateCandidateAsync(titleId, actor, reference, ct);
        if (current.IsError) return current.Errors;
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await unitOfWork.FlushAsync(ct);
        var title = await titles.GetWithCollectionsAsync(titleId, ct);
        var media = title!.Media.Single(item => item.FileId == stored.File.Id && item.Type == type && item.SourceId == original.SourceId);
        await transaction.CommitAsync(ct);
        return new TitleMediaRef { Id = IdCoder.Encode(media.Id), Type = media.Type.ToString(),
            SourceId = media.SourceId, Attribution = media.Attribution, SourcePageUrl = media.SourcePageUrl, IsPrimary = media.IsPrimary, Url = $"/media/{IdCoder.Encode(media.Id)}" };
    }
}
