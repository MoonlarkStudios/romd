using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Downloads provider media over HTTP into a temporary file and stages it for CAS publication.
/// </summary>
public sealed class HttpEnrichmentMediaStager(
    IFileStorageService fileStorage,
    ITempFileFactory tempFileFactory,
    HttpClient httpClient,
    ILogger<HttpEnrichmentMediaStager> logger) : IEnrichmentMediaStager
{
    public async Task<StagedTitleMedia?> StageAsync(
        int titleId,
        string providerId,
        MediaType mediaType,
        string url,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Downloading {MediaType} media from {Url}", mediaType, url);

        try
        {
            // Stream provider bodies directly into the bounded staging store. With headers-only
            // completion HttpClient's timeout stops at the headers, so retain it through the copy.
            using var downloadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadBudget.CancelAfter(httpClient.Timeout);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                downloadBudget.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to download media from {Url}: {StatusCode}",
                    url, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(downloadBudget.Token);
            var tempFile = await tempFileFactory.CreateAsync(stream, downloadBudget.Token);
            try
            {
                var contentType = await DetectContentTypeAsync(tempFile, cancellationToken);
                var staged = await fileStorage.StageAsync(tempFile, cancellationToken);

                logger.LogDebug("Staged {MediaType} media for title {TitleId}", mediaType, titleId);

                return new StagedTitleMedia(mediaType, providerId, contentType, url, staged, tempFile);
            }
            catch
            {
                await tempFile.DisposeAsync();
                throw;
            }
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
