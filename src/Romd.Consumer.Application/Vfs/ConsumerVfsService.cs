using ErrorOr;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

/// <summary>
/// Application orchestration for code-defined layouts. One scoped catalog lookup per request
/// authorizes both file metadata and content; layout-supplied file metadata is never opened.
/// </summary>
public sealed class ConsumerVfsService(
    VfsLayoutRegistry layouts, IConsumerVfsRepository repository, IConsumerVfsContentResolver content)
{
    public async Task<ErrorOr<ConsumerVfsRead>> ReadAsync(ConsumerLibraryScope scope, string key, string path,
        bool includeChildren, int maximumChildren, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored)
    {
        if (!VfsFormats.IsValid(format))
            return VfsConnectionErrors.InvalidFormat();
        if (layouts.Find(key) is not { } layout)
            return ConsumerVfsErrors.NotFound();
        if (layout.LocateFile(path) is { } locator)
        {
            var file = await repository.GetFileAsync(scope, locator.ReleaseId, locator.RelativePath, ct, format);
            if (file is ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found found)
            {
                if (layout.FilePath(found.Value) is not { } canonical)
                    return ConsumerVfsErrors.InvalidLayout();
                if (canonical != path)
                    return ConsumerVfsErrors.NotFound();
                ConsumerVfsFile resolved = found.Value.File;
                return new ConsumerVfsRead(
                    new(new(path, resolved.RelativePath.Split('/')[^1], resolved), []), resolved);
            }
            if (file is not ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound)
                return ConsumerVfsErrors.FromRead(file);
            // Not a release file: a directory inside the release still needs bounded enumeration.
        }

        var read = await layout.ReadAsync(scope, path, includeChildren, maximumChildren, ct, format);
        return read.IsError ? read.Errors : new ConsumerVfsRead(read.Value, null);
    }

    public Task<Stream?> OpenAsync(ConsumerVfsRead read, CancellationToken ct = default) =>
        read.Content is { } file ? content.OpenAsync(file, ct) : Task.FromResult<Stream?>(null);
}

/// <summary>A layout read plus any file the service itself authorized for content access.</summary>
public sealed class ConsumerVfsRead
{
    internal ConsumerVfsRead(VfsLayoutRead listing, ConsumerVfsFile? content)
    {
        Listing = listing;
        Content = content;
    }

    public VfsLayoutRead Listing { get; }

    /// <summary>Null for directories and for layout-supplied file metadata.</summary>
    public ConsumerVfsFile? Content { get; }
}
