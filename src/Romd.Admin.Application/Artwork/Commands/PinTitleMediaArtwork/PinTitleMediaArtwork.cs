using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Commands.PinTitleMediaArtwork;

public sealed record PinTitleMediaArtworkCommand(int TitleId, ArtworkRole Role, int MediaId,
    RevisionPrecondition IfMatch, int FocalX = 50, int FocalY = 50) : ICommand<ArtworkCurationState>;

/// <summary>Retains a validated media snapshot and pins it without changing the original media's type.</summary>
public sealed class PinTitleMediaArtworkCommandHandler(IArtworkCurationRepository repository,
    IArtworkImageProcessor processor, IFileStorageService files, IUnitOfWork unitOfWork,
    ILogger<PinTitleMediaArtworkCommandHandler> logger) : ICommandHandler<PinTitleMediaArtworkCommand, ArtworkCurationState>
{
    public async Task<ErrorOr<ArtworkCurationState>> HandleAsync(PinTitleMediaArtworkCommand command, CancellationToken ct = default)
    {
        if (command.FocalX is < 0 or > 100 || command.FocalY is < 0 or > 100 || !Enum.IsDefined(command.Role))
            return ArtworkCurationErrors.InvalidRequest();
        var media = await repository.FindMediaAsync(command.TitleId, command.MediaId, ct);
        if (media is null)
            return ArtworkCurationErrors.InvalidRequest();
        var bytes = await ReadOriginalAsync(media.FileId, ct);
        if (bytes.IsError)
            return bytes.Errors;
        var processed = await processor.ProcessAsync(bytes.Value, command.Role, ct);
        if (processed.IsError)
            return processed.Errors;

        // Reading, decoding and compression finish before the title or file locks are acquired.
        var prepared = await PreparedArtwork.StageAsync(files, bytes.Value, processed.Value, ct);
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<ArtworkCurationState>(async token =>
            {
                var locked = await repository.LockSelectionAsync(command.TitleId, command.Role, command.IfMatch, token);
                if (locked.IsError)
                    return locked.Errors;
                var currentMedia = await repository.FindMediaAsync(command.TitleId, command.MediaId, token);
                if (currentMedia?.FileId != media.FileId)
                    return ArtworkCurationErrors.SelectionChanged();

                var publication = await files.PublishStagedAsync(prepared.Files, token);
                if (publication.RequiresFlush)
                    await unitOfWork.FlushAsync(token);
                var content = prepared.Retain(publication.ReadAfterFlush(), null, null);
                await repository.StageLocalAssetAsync(command.TitleId, command.Role, media.SourceId, content, token);
                await unitOfWork.FlushAsync(token);
                var gallery = await repository.GetGalleryAsync(command.TitleId, token);
                if (gallery.IsError)
                    return gallery.Errors;
                var asset = gallery.Value.Single(item => item.Role == command.Role && item.SourceId == media.SourceId &&
                    item.ProviderAssetId is null && item.Original.ContentVersion == content.Original.Hash.ToString());
                var pinned = await repository.StagePinAsync(command.TitleId, command.Role, asset.Id, command.IfMatch,
                    token, command.FocalX, command.FocalY);
                if (pinned.IsError)
                    return pinned.Errors;
                // Read this write before releasing its title lock, so its body and ETag describe the same role.
                await unitOfWork.FlushAsync(token);
                var states = await repository.GetStatesAsync(command.TitleId, token);
                if (states.IsError)
                    return states.Errors;
                return states.Value.Single(state => state.Role == command.Role);
            }, logger, ct);
        }
        finally
        {
            try
            { await prepared.DisposeAsync(); }
            catch (Exception error)
            {
                // Resource cleanup cannot replace a rejection, primary failure, or confirmed durable response.
                logger.LogWarning(error, "Artwork pin preparation cleanup failed for title {TitleId}", command.TitleId);
            }
        }
    }

    private async Task<ErrorOr<byte[]>> ReadOriginalAsync(int fileId, CancellationToken ct)
    {
        await using var stream = await files.RetrieveByIdAsync(fileId, ct);
        if (stream is null)
            return ArtworkCurationErrors.InvalidRequest();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 20 * 1024 * 1024)
                return ArtworkImageErrors.TooLarge();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }
}
