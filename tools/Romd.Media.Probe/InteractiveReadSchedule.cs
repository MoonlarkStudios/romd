using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Romd.Media.Probe;

/// <summary>Fixed arrival schedule with bounded outstanding work and explicit missed arrivals.</summary>
internal static class InteractiveReadSchedule
{
    internal sealed record Result(int Scheduled, int Started, int Missed, int PeakOutstanding);

    internal static async Task<Result> RunAsync(TimeSpan duration, TimeSpan interval, int maximumOutstanding,
        Func<int, long, CancellationToken, Task> operation, Action<int> missed, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1) || interval < TimeSpan.FromMilliseconds(10) || interval > duration ||
            maximumOutstanding is < 1 or > 32 || duration.TotalMilliseconds / interval.TotalMilliseconds > 10_000)
            throw new ArgumentException("Bounded fixed-arrival schedule required.");
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(missed);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(maximumOutstanding, maximumOutstanding);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new HashSet<Task>();
        int scheduledCount = (int)Math.Ceiling(duration.TotalMilliseconds / interval.TotalMilliseconds);
        int startedCount = 0, missedCount = 0, outstanding = 0, peak = 0;
        long started = Stopwatch.GetTimestamp();
        long step = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        try
        {
            for (int index = 0; index < scheduledCount; index++)
            {
                long scheduled = started + index * step;
                await DelayUntilAsync(scheduled);
                run.Token.ThrowIfCancellationRequested();
                running.RemoveWhere(task => task.IsCompleted);
                if (index > 0 && Stopwatch.GetTimestamp() - scheduled >= step || !slots.Wait(0))
                {
                    missedCount++;
                    missed(index);
                    continue;
                }
                startedCount++;
                int active = Interlocked.Increment(ref outstanding);
                peak = Math.Max(peak, active);
                running.Add(InvokeAsync(index, scheduled));
            }
            await DelayUntilAsync(started + (long)(duration.TotalSeconds * Stopwatch.Frequency));
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure.TrySetResult(error);
            await run.CancelAsync();
        }
        finally
        {
            if (failure.Task.IsCompleted || ct.IsCancellationRequested)
                await run.CancelAsync();
            await Task.WhenAll(running);
        }
        if (failure.Task.IsCompleted)
            ExceptionDispatchInfo.Capture(await failure.Task).Throw();
        ct.ThrowIfCancellationRequested();
        return new(scheduledCount, startedCount, missedCount, peak);

        async Task InvokeAsync(int index, long scheduled)
        {
            try
            { await operation(index, scheduled, run.Token); }
            catch (Exception error)
            {
                failure.TrySetResult(error);
                await run.CancelAsync();
            }
            finally
            {
                Interlocked.Decrement(ref outstanding);
                slots.Release();
            }
        }

        async Task DelayUntilAsync(long scheduled)
        {
            double remaining = (scheduled - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            while (remaining > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, remaining)), run.Token);
                remaining = (scheduled - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            }
        }
    }
}
