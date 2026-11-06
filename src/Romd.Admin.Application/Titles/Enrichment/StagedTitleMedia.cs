using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Downloaded title media awaiting the mutation that persists its title, where the file row and the
///     media reference commit together. Owns the staged file and the download it may reread.
/// </summary>
public sealed class StagedTitleMedia(
    MediaType type,
    string sourceId,
    string contentType,
    string sourceUrl,
    IStagedFile file,
    IAsyncDisposable download) : IAsyncDisposable
{
    public MediaType Type { get; } = type;
    public string SourceId { get; } = sourceId;
    public IStagedFile File { get; } = file;

    public TitleMedia ToTitleMedia(int titleId, int fileId, DateTimeOffset createdAt) =>
        TitleMedia.CreateNew(titleId, Type, fileId, SourceId, contentType, createdAt, sourceUrl);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await File.DisposeAsync();
        }
        finally
        {
            await download.DisposeAsync();
        }
    }
}
