using System.Diagnostics;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal sealed record VfsProbeProcessCompletion(int ProcessId, int ExitCode, bool TimedOut, bool Canceled);

/// <summary>Joins an owned probe child, including after timeout or cancellation.</summary>
internal static class VfsProbeProcess
{
    public static async Task<VfsProbeProcessCompletion> WaitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.Id, process.ExitCode, false, false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await StopAsync(process, CancellationToken.None);
            return new(process.Id, process.ExitCode, !ct.IsCancellationRequested, ct.IsCancellationRequested);
        }
    }

    public static async Task StopAsync(Process process, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!process.HasExited)
        {
            try
            { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }
}
