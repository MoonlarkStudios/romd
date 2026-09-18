using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;

/// <summary>
///     Command to delete a media file from a title.
///     If the deleted media was primary, the system automatically
///     promotes the next best candidate based on priority rules.
/// </summary>
public sealed record DeleteTitleMediaCommand(
    int TitleId,
    int MediaId
) : ICommand<TitleDetail>;

public sealed class DeleteTitleMediaCommandHandler : ICommandHandler<DeleteTitleMediaCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleRepository _titleRepository;
    private readonly IFileStorageService _fileStorage;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly EnrichmentOptions _enrichmentOptions;
    private readonly ILogger<DeleteTitleMediaCommandHandler> _logger;

    public DeleteTitleMediaCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        IFileStorageService fileStorage,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        IOptions<EnrichmentOptions> enrichmentOptions,
        ILogger<DeleteTitleMediaCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _fileStorage = fileStorage;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _enrichmentOptions = enrichmentOptions.Value;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(DeleteTitleMediaCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        var title = await _titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        var media = title.GetMediaById(command.MediaId);
        if (media is null)
        {
            return CatalogErrors.MediaNotFound(command.MediaId);
        }

        var wasPrimary = media.IsPrimary;
        var mediaType = media.Type;
        var fileId = media.FileId;

        title.RemoveMedia(command.MediaId);

        if (wasPrimary)
        {
            var sourcePriority = _enrichmentOptions.GetSourcePriorityOrder();
            title.RecalculatePrimaryMedia(sourcePriority);
        }

        await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
        {
            await _titleRepository.UpdateStagedAsync(title, ct);
            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
            await _unitOfWork.FlushAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await _fileStorage.DeleteIfUnreferencedAsync(fileId, ct);

        _logger.LogInformation(
            "Removed {MediaType} media (ID: {MediaId}) from title '{TitleName}' (ID: {TitleId})",
            mediaType, command.MediaId, title.Name, title.Id);

        var detailData = await _titleRepository.GetTitleDetailAsync(command.TitleId, ct);
        if (detailData is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        return TitleDetailMapper.ToContract(detailData, systemKeys);
    }
}
