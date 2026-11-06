using System.Runtime.ExceptionServices;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal static class VfsAdmissionCleanup
{
    public static async Task RunAsync(CancellationTokenSource cancellation,
        IEnumerable<IDisposable> leases, IEnumerable<Task<HttpResponseMessage>> pending)
    {
        ExceptionDispatchInfo? failure = null;
        try
        { await cancellation.CancelAsync(); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        foreach (var lease in leases)
        {
            try
            { lease.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        // Observe every request even if an earlier request or response disposal faults.
        // Throw only after all owned work has finished, so fixture disposal cannot race it.
        foreach (var read in pending)
        {
            try
            { using var response = await read; }
            catch (OperationCanceledException) when (read.IsCanceled) { }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        failure?.Throw();
    }
}
