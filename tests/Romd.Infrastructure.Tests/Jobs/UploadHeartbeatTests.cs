using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class UploadHeartbeatTests
{
    [Fact]
    public async Task WithHeartbeatAsync_LongOperation_CheckpointsBeforeCompletion()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = UploadJobExecutor.WithHeartbeatAsync(
            token => finish.Task.WaitAsync(token),
            _ => { checkpointed.TrySetResult(); return Task.CompletedTask; }, CancellationToken.None);
        try
        {
            await checkpointed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            running.IsCompleted.ShouldBeFalse();
        }
        finally { finish.TrySetResult(); await running; }
    }

    [Fact]
    public async Task WithHeartbeatAsync_CheckpointFails_CancelsWorkAndSurfacesFailure()
    {
        var cancelled = false;
        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            UploadJobExecutor.WithHeartbeatAsync(async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled = token.IsCancellationRequested; }
            }, _ => throw new InvalidOperationException("Checkpoint failed"), CancellationToken.None));
        exception.Message.ShouldBe("Checkpoint failed");
        cancelled.ShouldBeTrue();
    }
}
