using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

/// <summary>
///     Collects a validated provider original into the title's media without changing artwork selections. The new
///     media is a member of the core title family, so the import runs under the title revision the curator read
///     (<paramref name="ifMatch" /> of <see cref="ImportAsync" />) and rotates it.
/// </summary>
public sealed class GalleryArtworkService(IArtworkBrowsingService browsing, IArtworkAssetSource source,
    IArtworkImageProcessor processor, IFileStorageService storage, ITitleRowLock titleLock, ITitleMediaStore mediaStore,
    ITitleReader titleReader, ITitleMediaReader titleMedia, IUnitOfWork unitOfWork, IAdminEventOutbox outbox, TimeProvider clock, ILogger<GalleryArtworkService> logger)
{
    public async Task<ErrorOr<UploadedTitleMedia>> ImportAsync(int titleId, Guid actor, string candidateId, string reference,
        RevisionPrecondition ifMatch, CancellationToken ct)
    {
        // A stale curator fails before the download. The compare under the title lock below is authoritative.
        var shown = await titleReader.GetRevisionAsync(titleId, ct);
        if (shown is null)
            return CatalogErrors.TitleNotFound();
        if (!ifMatch.IsSatisfiedBy(shown.Value))
            return CatalogErrors.TitlePreconditionFailed();
        var validation = await browsing.ValidateCandidateAsync(titleId, actor, reference, ct);
        if (validation.IsError)
            return validation.Errors;
        if (ArtworkCandidateIds.Of(validation.Value.TrustedAssetUrl) != candidateId)
            return ArtworkCurationErrors.CandidateMismatch();
        var candidate = validation.Value;
        var downloaded = await source.DownloadAsync(candidate.ProviderId, candidate.GameId, candidate.AssetId,
            candidate.Role, candidate.TrustedAssetUrl, ct);
        if (downloaded.IsError)
            return downloaded.Errors;
        var image = await processor.ProcessAsync(downloaded.Value.Bytes, candidate.Role, ct);
        if (image.IsError)
            return image.Errors;
        await using var content = new MemoryStream(downloaded.Value.Bytes, false);
        var staged = await storage.StageAsync(content, ct);
        try
        {
            var type = candidate.MediaType ?? candidate.Role switch
            {
                ArtworkRole.Poster => MediaType.Cover,
                ArtworkRole.Hero or ArtworkRole.Backdrop => MediaType.Background,
                ArtworkRole.Logo => MediaType.Logo,
                _ => throw new ArgumentOutOfRangeException(nameof(candidate.Role))
            };
            return await unitOfWork.ExecuteInTransactionAsync<UploadedTitleMedia>(async token =>
            {
                // Title before files, the order every title media and artwork writer uses. The lock holds the revision
                // still while the precondition is compared and the media appended.
                var revision = await titleLock.LockRevisionAsync(titleId, token);
                if (revision is null)
                    return CatalogErrors.TitleNotFound();
                if (!ifMatch.IsSatisfiedBy(revision.Value))
                    return CatalogErrors.TitlePreconditionFailed();
                var publication = await storage.PublishStagedAsync(staged, token);
                if (publication.RequiresFlush)
                    await unitOfWork.FlushAsync(token);
                var stored = publication.ReadAfterFlush();
                var original = TitleMedia.CreateNew(titleId, type, stored.File.Id, $"gallery:{candidate.ProviderId}",
                    image.Value.ContentType, clock.GetUtcNow(), candidate.TrustedAssetUrl,
                    attribution: candidate.Attribution, sourcePageUrl: candidate.SourcePageUrl);
                var appended = await mediaStore.AppendGalleryMediaStagedAsync(original, token);
                // The download may have outlived a provider match change. Roll back the append in that case.
                var current = await browsing.ValidateCandidateAsync(titleId, actor, reference, token);
                if (current.IsError)
                    return current.Errors;
                // A repeat stores nothing new.
                if (appended.Added)
                    await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, token);
                await unitOfWork.FlushAsync(token);
                var media = (await titleMedia.ListAsync(titleId, token)).Single(item => item.FileId == stored.File.Id && item.Type == type && item.SourceId == original.SourceId);
                return new UploadedTitleMedia(new TitleMediaRef
                {
                    Id = IdCoder.Encode(media.Id),
                    Type = media.Type.ToString(),
                    SourceId = media.SourceId,
                    Attribution = media.Attribution,
                    SourcePageUrl = media.SourcePageUrl,
                    IsPrimary = media.IsPrimary,
                    Url = $"/media/{IdCoder.Encode(media.Id)}"
                },
                    appended.TitleRevision.ToString(), appended.Added);
            }, logger, ct);
        }
        finally
        {
            await MediaUploadCleanup.DisposeAsync(staged, logger, titleId);
        }
    }
}
