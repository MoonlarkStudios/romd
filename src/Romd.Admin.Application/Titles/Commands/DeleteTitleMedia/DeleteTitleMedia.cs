using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;

/// <summary>
///     Command to delete a media file from a title, the one delete of a title media item. If the deleted media was
///     primary, the system automatically promotes the next best candidate based on priority rules. The image also
///     leaves the title's artwork: its assets are rejected, its source URL is never acquired again, and roles showing
///     it return to automatic.
/// </summary>
public sealed record DeleteTitleMediaCommand(
    int TitleId,
    int MediaId,
    RevisionPrecondition IfMatch
) : ICommand<TitleDetail>;

public sealed class DeleteTitleMediaCommandHandler : ICommandHandler<DeleteTitleMediaCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleAggregateReader _titleAggregates;
    private readonly ITitleDetailReader _titleDetails;
    private readonly ITitleMediaStore _mediaStore;
    private readonly IArtworkCurationRepository _artwork;
    private readonly IFileStorageService _fileStorage;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly EnrichmentOptions _enrichmentOptions;
    private readonly ILogger<DeleteTitleMediaCommandHandler> _logger;

    public DeleteTitleMediaCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleAggregateReader titleAggregates,
        ITitleDetailReader titleDetails,
        ITitleMediaStore mediaStore,
        IArtworkCurationRepository artwork,
        IFileStorageService fileStorage,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        IOptions<EnrichmentOptions> enrichmentOptions,
        ILogger<DeleteTitleMediaCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleAggregates = titleAggregates;
        _titleDetails = titleDetails;
        _mediaStore = mediaStore;
        _artwork = artwork;
        _fileStorage = fileStorage;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _enrichmentOptions = enrichmentOptions.Value;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(DeleteTitleMediaCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        Domain.Catalog.Title? title;
        try
        {
            title = await _titleAggregates.GetAggregateAsync(command.TitleId, ct);
        }
        catch (PersistenceConflictException)
        {
            ct.ThrowIfCancellationRequested();
            return CatalogErrors.TitleReadConflict();
        }
        if (title is null)
        {
            return CatalogErrors.TitleNotFound();
        }

        if (!command.IfMatch.IsSatisfiedBy(title.Revision))
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        var media = title.GetMediaById(command.MediaId);
        if (media is null)
        {
            return CatalogErrors.MediaNotFound();
        }

        var wasPrimary = media.IsPrimary;
        var mediaType = media.Type;
        var fileId = media.FileId;
        var sourceUrl = media.SourceUrl;

        title.RemoveMedia(command.MediaId);

        if (wasPrimary)
        {
            var sourcePriority = _enrichmentOptions.GetSourcePriorityOrder();
            title.RecalculatePrimaryMedia(sourcePriority);
        }

        ErrorOr<TitleDetailData> mutation;
        try
        {
            mutation = await _unitOfWork.ExecuteInTransactionAsync<TitleDetailData>(async token =>
            {
                // Deletes only this row and persists the recalculated primaries, after the revision check.
                await _mediaStore.RemoveMediaStagedAsync(title, command.MediaId, token);
                await _artwork.StageTitleMediaRemovalAsync(command.TitleId, command.MediaId, fileId, sourceUrl, token);
                await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, token);
                await _unitOfWork.FlushAsync(token);
                // Read the response before commit, so a writer that commits after this delete is in neither its
                // body nor its ETag.
                var detailData = await _titleDetails.GetDetailAsync(command.TitleId, token);
                return detailData is null ? CatalogErrors.TitleNotFound() : detailData;
            }, _logger, ct);
        }
        catch (PersistenceConflictException)
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        if (mutation.IsError)
        {
            return mutation.Errors;
        }

        try
        {
            await _fileStorage.DeleteIfUnreferencedAsync(fileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Title media deletion committed but immediate unreferenced-file cleanup failed " +
                "(Title ID: {TitleId}, Media ID: {MediaId}, File ID: {FileId}); recurring cleanup will retry",
                command.TitleId, command.MediaId, fileId);
        }

        _logger.LogInformation(
            "Removed {MediaType} media (ID: {MediaId}) from title '{TitleName}' (ID: {TitleId})",
            mediaType, command.MediaId, title.Name, title.Id);

        return TitleDetailMapper.ToContract(mutation.Value, systemKeys);
    }
}
