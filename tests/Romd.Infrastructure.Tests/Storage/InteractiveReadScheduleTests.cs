using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class InteractiveReadScheduleTests
{
    [Fact]
    public async Task RunAsync_OneStalledRequest_CountsEveryMissedArrivalAndJoinsStartedWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await InteractiveReadSchedule.RunAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20), 1,
            async (_, _, token) =>
            {
                await release.Task.WaitAsync(token);
                Interlocked.Increment(ref completed);
            }, index =>
            {
                if (index == 9)
                    release.TrySetResult();
            }, deadline.Token);

        result.Scheduled.ShouldBe(10);
        result.Started.ShouldBe(1);
        result.Missed.ShouldBe(9);
        result.PeakOutstanding.ShouldBe(1);
        completed.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_RequestFailure_CancelsAndJoinsTheOtherOutstandingRequest()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Should.ThrowAsync<InvalidDataException>(() => InteractiveReadSchedule.RunAsync(
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), 2, async (index, _, token) =>
            {
                if (index == 0)
                {
                    await secondStarted.Task.WaitAsync(token);
                    throw new InvalidDataException("injected range integrity failure");
                }
                Interlocked.Increment(ref active);
                secondStarted.TrySetResult();
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref active); }
            }, _ => { }, deadline.Token));

        error.Message.ShouldBe("injected range integrity failure");
        active.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_Cancellation_JoinsEveryStartedRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int active = 0;
        var run = InteractiveReadSchedule.RunAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), 2,
            async (_, _, token) =>
            {
                Interlocked.Increment(ref active);
                started.TrySetResult();
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref active); }
            }, _ => { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
        active.ShouldBe(0);
    }
}
