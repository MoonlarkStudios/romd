using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class WorkspaceInputCopyFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-upload-copy-failure-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_TransferAndCleanupBothFail_PreservesExactPrimaryAndCancellation(bool cancel)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        var id = Guid.NewGuid();
        string workspace = Path.Combine(_root, "temp", "jobs", id.ToString("N"));
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("original transfer fault");
        using var source = new CleanupBlockingStream(workspace, primary, cancel ? cancellation.Cancel : null);
        await using var lease = await new JobWorkspaceExclusion(options).AcquireAsync(id);

        var observed = await Record.ExceptionAsync(() => new UploadWorkspacePreparation(options)
            .PrepareAsync(new(id, "pack.zip"), source, lease, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        // The failed transfer replaced its own directory with a file. Directory.Delete must fail,
        // proving that the cleanup path executed without replacing the original exception.
        File.Exists(workspace).ShouldBeTrue();
        (await File.ReadAllTextAsync(workspace)).ShouldBe("cleanup blocker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToOwnedAsync_TransferAndTargetDisposalBothFail_PreservesPrimaryAndCallerSource(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("original transfer fault");
        using var source = new FailedSource(primary, cancel ? cancellation.Cancel : null);
        var target = new ObservedTarget(new IOException("target disposal failed"));

        var observed = await Record.ExceptionAsync(() => WorkspaceInputCopies.CopyToOwnedAsync(source, target, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        source.CanRead.ShouldBeTrue();
        target.DisposeCalls.ShouldBe(1);
        target.CanWrite.ShouldBeFalse();
        target.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task CopyToOwnedAsync_OnlyTargetDisposalFails_PropagatesExactCleanupFailure()
    {
        using var source = new MemoryStream([1, 2, 3]);
        var cleanup = new IOException("target disposal failed");
        var target = new ObservedTarget(cleanup);

        var observed = await Record.ExceptionAsync(() => WorkspaceInputCopies.CopyToOwnedAsync(source, target, default));

        observed.ShouldBeSameAs(cleanup);
        source.CanRead.ShouldBeTrue();
        target.DisposeCalls.ShouldBe(1);
        target.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task CopyToOwnedAsync_CompleteCopy_DisposesOnlyOwnedTarget()
    {
        using var source = new MemoryStream([1, 2, 3]);
        var target = new ObservedTarget(null);

        await WorkspaceInputCopies.CopyToOwnedAsync(source, target, default);

        source.CanRead.ShouldBeTrue();
        source.Position.ShouldBe(3);
        target.DisposeCalls.ShouldBe(1);
        target.CanWrite.ShouldBeFalse();
        target.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CleanupBlockingStream(string workspace, Exception primary, Action? cancel) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            await destination.WriteAsync(new byte[] { 1, 2 }, ct);
            await destination.DisposeAsync();
            Directory.Delete(workspace, recursive: true);
            await File.WriteAllTextAsync(workspace, "cleanup blocker", ct);
            cancel?.Invoke();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    private sealed class FailedSource(Exception primary, Action? cancel) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            cancel?.Invoke();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    private sealed class ObservedTarget(Exception? cleanup) : MemoryStream
    {
        public int DisposeCalls { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await base.DisposeAsync();
            if (cleanup is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }
}
