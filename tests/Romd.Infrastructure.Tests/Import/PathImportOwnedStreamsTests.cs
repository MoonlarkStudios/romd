using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathImportOwnedStreamsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteManifestOwnedAsync_WriteAndDisposalFail_PreservesExactPrimaryAndCancellationToken(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Manifest cancelled.", cancellation.Token) : new IOException("Manifest write failed.");
        var stream = new ManifestStream(primary, new IOException("Manifest cleanup failed."));
        var manifest = new ImportManifest(ImportManifest.CurrentVersion, "/allowed", "originals", true, []);

        var observed = await CaptureAsync(() => ImportManifest.WriteOwnedAsync(stream, manifest, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        stream.DisposeCount.ShouldBe(1);
        stream.CanRead.ShouldBeFalse();
    }

    [Fact]
    public async Task WriteManifestOwnedAsync_OnlyDisposalFails_ReportsCleanupFailureAfterWritingManifest()
    {
        var failure = new IOException("Manifest cleanup failed.");
        var stream = new ManifestStream(null, failure);
        var manifest = new ImportManifest(ImportManifest.CurrentVersion, "/allowed", "originals", true, []);

        var observed = await CaptureAsync(() => ImportManifest.WriteOwnedAsync(stream, manifest, default));

        observed.ShouldBeSameAs(failure);
        stream.ToArray().ShouldNotBeEmpty();
        stream.DisposeCount.ShouldBe(1);
        stream.CanRead.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyOwnedAsync_CopyFaultAndBothDisposalsFail_PreservesExactPrimaryAndCancellationToken(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Copy cancelled.", cancellation.Token) : new IOException("Copy failed.");
        List<string> events = [];
        var source = new ObservedStream("source", events, primary, new IOException("Source cleanup failed."));
        var target = new ObservedStream("target", events, cleanup: new IOException("Target cleanup failed."));

        var observed = await CaptureAsync(() => PathImportFiles.CopyOwnedAsync(() => source, () => target, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        events.ShouldBe(["copy", "target-dispose", "source-dispose"]);
        source.CanRead.ShouldBeFalse();
        target.CanRead.ShouldBeFalse();
    }

    [Fact]
    public async Task CopyOwnedAsync_TargetOpenFailsAndSourceDisposalFails_PreservesOpenFailureAndReleasesSource()
    {
        List<string> events = [];
        var source = new ObservedStream("source", events, cleanup: new IOException("Source cleanup failed."));
        var primary = new UnauthorizedAccessException("Target cannot open.");

        var observed = await CaptureAsync(() => PathImportFiles.CopyOwnedAsync(() => source, () => throw primary, default));

        observed.ShouldBeSameAs(primary);
        events.ShouldBe(["source-dispose"]);
        source.CanRead.ShouldBeFalse();
    }

    [Fact]
    public async Task CopyOwnedAsync_CopySucceedsAndBothDisposalsFail_ReportsFirstCleanupFaultAndJoinsBoth()
    {
        List<string> events = [];
        var source = new ObservedStream("source", events, cleanup: new IOException("Source cleanup failed."));
        var failure = new IOException("Target cleanup failed.");
        var target = new ObservedStream("target", events, cleanup: failure);

        var observed = await CaptureAsync(() => PathImportFiles.CopyOwnedAsync(() => source, () => target, default));

        observed.ShouldBeSameAs(failure);
        target.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        events.ShouldBe(["copy", "target-dispose", "source-dispose"]);
        source.CanRead.ShouldBeFalse();
        target.CanRead.ShouldBeFalse();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class ObservedStream(string name, List<string> events, Exception? copy = null, Exception? cleanup = null)
        : MemoryStream(name == "source" ? [1, 2, 3] : new byte[3])
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            events.Add("copy");
            return copy is not null ? Task.FromException(copy) : base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            events.Add($"{name}-dispose");
            await base.DisposeAsync();
            if (cleanup is not null)
                throw cleanup;
        }
    }

    private sealed class ManifestStream(Exception? writeFailure, Exception cleanupFailure) : MemoryStream
    {
        public int DisposeCount { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            writeFailure is null ? base.WriteAsync(buffer, cancellationToken) : ValueTask.FromException(writeFailure);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            writeFailure is null ? base.WriteAsync(buffer, offset, count, cancellationToken) : Task.FromException(writeFailure);
        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            throw cleanupFailure;
        }
    }
}
