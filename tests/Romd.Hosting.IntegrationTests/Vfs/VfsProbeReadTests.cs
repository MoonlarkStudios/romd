using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeReadTests
{
    [Fact]
    public async Task ObserveAsync_ObservationFails_RestoresCancelsAndJoinsReadBeforeThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new TimeoutException("Observation timed out.");
        bool restored = false;
        bool joined = false;
        Task<bool>? pending = null;
        var releaseRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ReadAsync(CancellationToken ct)
        {
            try
            { return await releaseRead.Task.WaitAsync(ct); }
            finally { joined = true; }
        }
        try
        {
            var observed = await Should.ThrowAsync<TimeoutException>(() => VfsProbeRead.ObserveAsync(
                ct => pending = ReadAsync(ct), _ => Task.FromException(failure), () => restored = true,
                cancellation.Token));
            observed.ShouldBeSameAs(failure);
            restored.ShouldBeTrue();
            joined.ShouldBeTrue("The failed observation must not leave an owned read active during unmount.");
            pending!.IsCompleted.ShouldBeTrue();
        }
        finally
        {
            releaseRead.TrySetResult(true);
            await cancellation.CancelAsync();
            if (pending is not null)
            {
                try
                { await pending; }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task ObserveAsync_Success_RestoresBeforeJoiningRead()
    {
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ReadAsync(CancellationToken ct)
        {
            await restored.Task.WaitAsync(ct);
            return true;
        }
        (await VfsProbeRead.ObserveAsync(ReadAsync, _ => Task.CompletedTask,
            () => restored.SetResult(), CancellationToken.None)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveAsync_CancellationOrRestoreFailure_JoinsReadAndPreservesFailure(bool restoreFails)
    {
        Exception failure = restoreFails ? new IOException("Restoration failed.")
            : new OperationCanceledException("Observation canceled.");
        bool joined = false;
        var releaseRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? pending = null;
        async Task<bool> ReadAsync(CancellationToken ct)
        {
            try
            { return await releaseRead.Task.WaitAsync(ct); }
            finally { joined = true; }
        }
        try
        {
            Exception? observed = null;
            try
            {
                await VfsProbeRead.ObserveAsync(ct => pending = ReadAsync(ct),
                    _ => restoreFails ? Task.CompletedTask : Task.FromException(failure),
                    () => { if (restoreFails) throw failure; }, CancellationToken.None);
            }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(failure);
            joined.ShouldBeTrue();
            pending!.IsCompleted.ShouldBeTrue();
        }
        finally
        {
            releaseRead.TrySetResult(true);
            if (pending is not null)
            {
                try
                { await pending; }
                catch (OperationCanceledException) { }
            }
        }
    }
}
