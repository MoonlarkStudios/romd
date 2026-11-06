using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Storage;
using System.Runtime.ExceptionServices;

namespace Romd.Infrastructure.Storage;

/// <summary>Publishes reference assets through the file ledger and reads them back from CAS.</summary>
public sealed class ReferenceBlobStore(IContentAddressableStore store, IFileStorageService files) : IReferenceBlobStore
{
    private readonly ReadOnlyReferenceBlobStore _reader = new(store);

    public async Task<IStagedReferencePublication> PublishStagedAsync(IReadOnlyList<byte[]> contents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ownedContents = contents.Select(bytes => bytes.ToArray()).ToArray();
        var staged = new List<IStagedFile>(contents.Count);
        var streams = new List<MemoryStream>(contents.Count);
        try
        {
            foreach (byte[] bytes in ownedContents)
            {
                var stream = new MemoryStream(bytes, writable: false);
                streams.Add(stream);
                staged.Add(await files.StageAsync(stream, ct));
            }
            var published = await files.PublishStagedAsync(staged, ct);
            return new Publication(published, staged, streams);
        }
        catch (Exception primary)
        {
            try
            { await DisposeStagingAsync(staged, streams); }
            catch (Exception cleanup) { primary.Data["Romd.ReferencePublication.CleanupFailure"] = cleanup; }
            throw;
        }
    }

    public Task<byte[]?> ReadAsync(string hash, CancellationToken ct) => _reader.ReadAsync(hash, ct);

    private static async ValueTask DisposeStagingAsync(IReadOnlyList<IStagedFile> staged, IReadOnlyList<MemoryStream> streams)
    {
        var errors = new List<Exception>();
        foreach (var file in staged)
        {
            try
            { await file.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        foreach (var stream in streams)
        {
            try
            { await stream.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Reference publication staging cleanup failed.", errors);
    }

    private sealed class Publication(IStagedFilePublicationBatch published, IReadOnlyList<IStagedFile> staged,
        IReadOnlyList<MemoryStream> streams) : IStagedReferencePublication
    {
        private bool _disposed;
        public bool RequiresFlush
        {
            get { ObjectDisposedException.ThrowIf(_disposed, this); return published.RequiresFlush; }
        }
        public IReadOnlyList<int> ReadAfterFlush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return published.ReadAfterFlush().Select(result => result.File.Id).ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await DisposeStagingAsync(staged, streams);
        }
    }
}
