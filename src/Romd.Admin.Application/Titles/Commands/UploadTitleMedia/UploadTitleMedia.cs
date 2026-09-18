using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.UploadTitleMedia;

/// <summary>
///     Command to upload a custom media file for a title.
///     User uploads are retained as alternatives; role assignment is a separate operation.
/// </summary>
public sealed record UploadTitleMediaCommand(
    int TitleId,
    MediaType Type,
    Stream Content,
    string FileName
) : ICommand<TitleMediaRef>;

/// <summary>
///     Handler for <see cref="UploadTitleMediaCommand" />.
///     Appends user media without replacing previous images or display assignments.
/// </summary>
public sealed class UploadTitleMediaCommandHandler : ICommandHandler<UploadTitleMediaCommand, TitleMediaRef>
{
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<UploadTitleMediaCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly ITempFileFactory _tempFileFactory;
    private readonly ITitleRepository _titleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public UploadTitleMediaCommandHandler(
        ITitleRepository titleRepository,
        IFileStorageService fileStorage,
        ITempFileFactory tempFileFactory,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILogger<UploadTitleMediaCommandHandler> logger)
    {
        _titleRepository = titleRepository;
        _fileStorage = fileStorage;
        _tempFileFactory = tempFileFactory;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleMediaRef>> HandleAsync(UploadTitleMediaCommand command,
        CancellationToken ct = default)
    {
        var title = await _titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        await using var tempFile = await _tempFileFactory.CreateAsync(command.Content, ct);
        string contentType = await DetectContentTypeAsync(tempFile, ct);

        var storeResult = await _fileStorage.StoreFromTempFileAsync(tempFile, ct);

        var media = TitleMedia.CreateNew(
            command.TitleId,
            command.Type,
            storeResult.File.Id,
            "user",
            contentType);

        TitleMedia savedMedia;
        await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
        {
            await _titleRepository.AddUserMediaStagedAsync(media, ct);
            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
            await _unitOfWork.FlushAsync(ct);
            var updated = await _titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
            savedMedia = updated!.Media.Where(item => item.SourceId == "user" && item.Type == command.Type && item.FileId == media.FileId)
                .MaxBy(item => item.Id)!;
            await transaction.CommitAsync(ct);
        }

        _logger.LogInformation(
            "Uploaded {MediaType} media for title '{TitleName}' (ID: {TitleId})",
            command.Type, title.Name, title.Id);

        return new TitleMediaRef
        {
            Id = IdCoder.Encode(savedMedia.Id),
            Type = savedMedia.Type.ToString(),
            Url = $"/media/{IdCoder.Encode(savedMedia.Id)}",
            SourceId = savedMedia.SourceId,
            IsPrimary = savedMedia.IsPrimary
        };
    }

    private static async Task<string> DetectContentTypeAsync(ITempFile tempFile, CancellationToken ct)
    {
        await using var stream = tempFile.OpenRead();
        byte[] buffer = new byte[12];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        if (bytesRead >= 3 && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytesRead >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
        {
            return "image/png";
        }

        if (bytesRead >= 6 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytesRead >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
            && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytesRead >= 2 && buffer[0] == 0x42 && buffer[1] == 0x4D)
        {
            return "image/bmp";
        }

        return "application/octet-stream";
    }
}
