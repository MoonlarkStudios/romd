using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Downloads media from provider URLs and stores in CAS.
/// </summary>
public sealed class MediaDownloader(
    IFileStorageService fileStorage,
    ITempFileFactory tempFileFactory,
    HttpClient httpClient,
    ILogger<MediaDownloader> logger)
{
    /// <summary>
    ///     Downloads a media asset and stores it in CAS.
    ///     Returns the created TitleMedia, or null if download failed.
    /// </summary>
    public async Task<TitleMedia?> DownloadAndStoreAsync(
        int titleId,
        string providerId,
        MediaType mediaType,
        string url,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Downloading {MediaType} media from {Url}", mediaType, url);

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to download media from {Url}: {StatusCode}",
                    url, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var tempFile = await tempFileFactory.CreateAsync(stream, cancellationToken);

            var contentType = await DetectContentTypeAsync(tempFile, cancellationToken);
            var storeResult = await fileStorage.StoreFromTempFileAsync(tempFile, cancellationToken);

            var media = TitleMedia.CreateNew(
                titleId,
                mediaType,
                storeResult.File.Id,
                sourceId: providerId,
                contentType: contentType,
                sourceUrl: url);

            logger.LogDebug(
                "Stored {MediaType} media for title {TitleId} (FileId: {FileId})",
                mediaType, titleId, storeResult.File.Id);

            return media;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to download {MediaType} media from {Url}",
                mediaType, url);
            return null;
        }
    }

    private static async Task<string> DetectContentTypeAsync(ITempFile tempFile, CancellationToken ct)
    {
        await using var stream = tempFile.OpenRead();
        var buffer = new byte[12];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        if (bytesRead >= 3 && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
            return "image/jpeg";
        if (bytesRead >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
            return "image/png";
        if (bytesRead >= 6 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46)
            return "image/gif";
        if (bytesRead >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
            && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
            return "image/webp";
        if (bytesRead >= 2 && buffer[0] == 0x42 && buffer[1] == 0x4D)
            return "image/bmp";

        return "application/octet-stream";
    }
}
