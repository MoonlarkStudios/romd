using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Management.Models;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.UploadTitleMedia;

/// <summary>
///     Command to upload a custom media file for a title.
///     User uploads are retained as alternatives; role assignment is a separate operation.
///     <paramref name="IfMatch" /> is the title revision the curator read.
/// </summary>
public sealed record UploadTitleMediaCommand(
    int TitleId,
    MediaType Type,
    Stream Content,
    string FileName,
    RevisionPrecondition IfMatch
) : ICommand<UploadedTitleMedia>;

/// <summary>
///     The media an upload or gallery import answers with, the title's revision after the write, and whether it
///     created the item rather than answering a repeat of the same content with the existing one, which keeps the
///     revision.
/// </summary>
public sealed record UploadedTitleMedia(TitleMediaRef Media, string TitleRevision, bool Created);

/// <summary>
///     Handler for <see cref="UploadTitleMediaCommand" />.
///     Appends user media without replacing previous images or display assignments.
/// </summary>
public sealed class UploadTitleMediaCommandHandler : ICommandHandler<UploadTitleMediaCommand, UploadedTitleMedia>
{
    private readonly TimeProvider _clock;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<UploadTitleMediaCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly ITempFileFactory _tempFileFactory;
    private readonly ITitleRowLock _titleLock;
    private readonly ITitleMediaStore _mediaStore;
    private readonly ITitleReader _titleReader;
    private readonly ITitleMediaReader _titleMedia;
    private readonly IUnitOfWork _unitOfWork;

    public UploadTitleMediaCommandHandler(
        ITitleRowLock titleLock,
        ITitleMediaStore mediaStore,
        ITitleReader titleReader,
        ITitleMediaReader titleMedia,
        IFileStorageService fileStorage,
        ITempFileFactory tempFileFactory,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILogger<UploadTitleMediaCommandHandler> logger,
        TimeProvider clock)
    {
        _titleLock = titleLock;
        _mediaStore = mediaStore;
        _titleReader = titleReader;
        _titleMedia = titleMedia;
        _fileStorage = fileStorage;
        _tempFileFactory = tempFileFactory;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ErrorOr<UploadedTitleMedia>> HandleAsync(UploadTitleMediaCommand command,
        CancellationToken ct = default)
    {
        var title = await _titleReader.GetSummaryAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound();
        }

        ITempFile? tempFile = null;
        IStagedFile? staged = null;
        try
        {
            tempFile = await _tempFileFactory.CreateAsync(command.Content, ct);
            string contentType = await DetectContentTypeAsync(tempFile, ct);
            staged = await _fileStorage.StageAsync(tempFile, ct);
            var result = await _unitOfWork.ExecuteInTransactionAsync<UploadedTitleMedia>(async token =>
            {
                // Title before files, the order every title media and artwork writer uses. The lock holds the
                // compared revision until commit, so nothing is published for a stale curator.
                var current = await _titleLock.LockRevisionAsync(command.TitleId, token);
                if (current is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                if (!command.IfMatch.IsSatisfiedBy(current.Value))
                {
                    return CatalogErrors.TitlePreconditionFailed();
                }

                var publication = await _fileStorage.PublishStagedAsync(staged, token);
                if (publication.RequiresFlush)
                    await _unitOfWork.FlushAsync(token);
                var storeResult = publication.ReadAfterFlush();
                var media = TitleMedia.CreateNew(
                    command.TitleId,
                    command.Type,
                    storeResult.File.Id,
                    "user",
                    contentType, _clock.GetUtcNow());
                var appended = await _mediaStore.AddUserMediaStagedAsync(media, token);
                if (appended.Added)
                {
                    await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, token);
                }
                await _unitOfWork.FlushAsync(token);
                var savedMedia = (await _titleMedia.ListAsync(command.TitleId, token)).Where(item => item.SourceId == "user" && item.Type == command.Type && item.FileId == media.FileId)
                    .MaxBy(item => item.Id)!;
                return new UploadedTitleMedia(new TitleMediaRef
                {
                    Id = IdCoder.Encode(savedMedia.Id),
                    Type = savedMedia.Type.ToString(),
                    Url = $"/media/{IdCoder.Encode(savedMedia.Id)}",
                    SourceId = savedMedia.SourceId,
                    IsPrimary = savedMedia.IsPrimary
                }, appended.TitleRevision.ToString(), appended.Added);
            }, _logger, ct);

            if (!result.IsError)
                _logger.LogInformation(
                    "Uploaded {MediaType} media for title '{TitleName}' (ID: {TitleId})",
                    command.Type, title.Name, title.Id);
            return result;
        }
        finally
        {
            await MediaUploadCleanup.DisposeAsync(staged, _logger, command.TitleId);
            await MediaUploadCleanup.DisposeAsync(tempFile, _logger, command.TitleId);
        }
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
