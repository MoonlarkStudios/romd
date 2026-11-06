using Romd.Admin.Application.Storage.Files;

namespace Romd.Admin.Application.Artwork;

/// <summary>
///     Owns the original and variants staged before taking database locks. Publication and its transaction
///     belong to the caller; source streams remain alive until every staged file has been disposed.
/// </summary>
public sealed class PreparedArtwork : IAsyncDisposable
{
    private readonly IReadOnlyList<PreparedFile> _files;
    private bool _disposed;

    private PreparedArtwork(List<PreparedFile> files)
    {
        _files = files.AsReadOnly();
        Files = Array.AsReadOnly(files.Select(file => file.Staged).ToArray());
    }

    public IReadOnlyList<IStagedFile> Files { get; }

    public static async Task<PreparedArtwork> StageAsync(IFileStorageService storage, byte[] original,
        ProcessedArtworkImage image, CancellationToken ct)
    {
        var files = new List<PreparedFile>(image.Variants.Count + 1);
        try
        {
            files.Add(await StageFileAsync(storage, original, image.ContentType, image.Width, image.Height, "original", ct));
            foreach (var variant in image.Variants)
                files.Add(await StageFileAsync(storage, variant.EncodedBytes, variant.ContentType, variant.Width,
                    variant.Height, variant.Name, ct));
            return new PreparedArtwork(files);
        }
        catch (Exception primary)
        {
            try
            { await DisposeFilesAsync(files); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
    }

    /// <summary>Maps results in the same order as <see cref="Files" /> after the owner has read its publication receipt.</summary>
    public RetainedArtworkContent Retain(IReadOnlyList<FileStoreResult> published, string? attribution, string? sourcePageUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (published.Count != _files.Count)
            throw new ArgumentException("Publication must return one result for every prepared file.", nameof(published));
        var retained = _files.Zip(published, (file, result) => new RetainedArtworkFile(result.File.Id,
            result.File.Sha256, file.ContentType, file.Width, file.Height, file.Name)).ToArray();
        return new RetainedArtworkContent(retained[0], Array.AsReadOnly(retained[1..]), attribution, sourcePageUrl);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisposeFilesAsync(_files);
    }

    private static async Task<PreparedFile> StageFileAsync(IFileStorageService storage, byte[] bytes,
        string contentType, int width, int height, string name, CancellationToken ct)
    {
        var source = new MemoryStream(bytes.ToArray(), writable: false);
        try
        { return new PreparedFile(await storage.StageAsync(source, ct), source, contentType, width, height, name); }
        catch { await source.DisposeAsync(); throw; }
    }

    private static async Task DisposeFilesAsync(IEnumerable<PreparedFile> files)
    {
        List<Exception>? failures = null;
        foreach (var file in files)
        {
            try
            { await file.Staged.DisposeAsync(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            finally { await file.Source.DisposeAsync(); }
        }
        if (failures is not null)
            throw new AggregateException("Prepared artwork cleanup failed.", failures);
    }

    private sealed record PreparedFile(IStagedFile Staged, MemoryStream Source, string ContentType, int Width, int Height, string Name);
}
