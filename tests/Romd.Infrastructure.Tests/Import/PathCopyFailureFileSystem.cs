using System.IO.Abstractions;
using NSubstitute;

namespace Romd.Infrastructure.Tests.Import;

/// <summary>Real file streams with deterministic faults after bytes reach a workspace target.</summary>
internal sealed class PathCopyFailureFileSystem
{
    internal IFileSystem FileSystem { get; } = Substitute.For<IFileSystem>();
    internal Dictionary<string, Exception> CopyFailures { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Exception> TargetDisposalFailures { get; } = new(StringComparer.Ordinal);
    internal List<ObservedFileStream> Streams { get; } = [];
    internal List<(string Path, byte[] Bytes)> DeleteAttempts { get; } = [];
    internal Exception? DeleteFailure { get; set; }
    internal bool IgnoreDelete { get; set; }
    internal Action? BeforeCopyFailure { get; set; }
    internal Action<string>? BeforeDelete { get; set; }

    internal PathCopyFailureFileSystem()
    {
        var drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(long.MaxValue);
        FileSystem.DriveInfo.New(Arg.Any<string>()).Returns(drive);
        var files = Substitute.For<IFile>();
        FileSystem.File.Returns(files);
        files.Open(Arg.Any<string>(), FileMode.Open, FileAccess.Read, FileShare.Read)
            .Returns(call => OpenSource(call.ArgAt<string>(0)));
        files.Create(Arg.Any<string>()).Returns(call => OpenTarget(call.Arg<string>()));
        files.Exists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        files.When(file => file.Delete(Arg.Any<string>())).Do(call => DeleteTarget(call.Arg<string>()));
    }

    private ObservedFileStream OpenSource(string path)
    {
        CopyFailures.TryGetValue(Path.GetFileName(path), out var failure);
        var stream = new ObservedFileStream(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            source: true, failure, null, BeforeCopyFailure);
        Streams.Add(stream);
        return stream;
    }

    private ObservedFileStream OpenTarget(string path)
    {
        TargetDisposalFailures.TryGetValue(Path.GetFileName(path), out var failure);
        var stream = new ObservedFileStream(File.Create(path), source: false, null, failure, null);
        Streams.Add(stream);
        return stream;
    }

    private void DeleteTarget(string path)
    {
        DeleteAttempts.Add((path, File.Exists(path) ? File.ReadAllBytes(path) : []));
        BeforeDelete?.Invoke(path);
        if (DeleteFailure is not null)
            throw DeleteFailure;
        if (!IgnoreDelete)
            File.Delete(path);
    }

    internal sealed class ObservedFileStream(FileStream inner, bool source, Exception? copyFailure,
        Exception? disposalFailure, Action? beforeCopyFailure) : FileSystemStream(inner, inner.Name, inner.IsAsync)
    {
        internal string FilePath { get; } = inner.Name;
        internal bool IsSource { get; } = source;
        internal int DisposeCount { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (copyFailure is null)
            {
                await base.CopyToAsync(destination, bufferSize, cancellationToken);
                return;
            }
            byte[] prefix = new byte[2];
            int count = await inner.ReadAsync(prefix, cancellationToken);
            await destination.WriteAsync(prefix.AsMemory(0, count), cancellationToken);
            await destination.FlushAsync(cancellationToken);
            beforeCopyFailure?.Invoke();
            throw copyFailure;
        }

        public override void Flush(bool flushToDisk) => inner.Flush(flushToDisk);

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            if (disposalFailure is not null)
                throw disposalFailure;
        }
    }
}
