using System.Diagnostics;
using Romd.Media.Processes;

namespace Romd.Infrastructure.Storage;

/// <summary>Platform-specific direct-child ownership; POSIX status belongs only to native waitpid.</summary>
internal sealed class MediaChildProcess : IDisposable
{
    private readonly Process? _managed;
    private readonly PosixMediaChild? _native;
    private MediaChildProcess(Process managed) => _managed = managed;
    private MediaChildProcess(PosixMediaChild native) => _native = native;
    internal Process Observer => _native?.Observer ?? _managed!;
    internal int Id => Observer.Id;
    internal Stream Input => _native?.Input ?? _managed!.StandardInput.BaseStream;
    internal Stream Output => _native?.Output ?? _managed!.StandardOutput.BaseStream;
    internal Stream Error => _native?.Error ?? _managed!.StandardError.BaseStream;
    internal Stream Ready => _native?.Ready ?? Output;
    internal bool HasExited => _native?.HasExited ?? _managed!.HasExited;
    internal int ExitCode => _native?.ExitCode ?? _managed!.ExitCode;
    internal Task WaitAsync(CancellationToken ct) => _native?.WaitAsync(ct) ?? _managed!.WaitForExitAsync(ct);
    internal void Terminate(MediaWorkerProcessLifetime lifetime)
    {
        if (_native is not null)
            lifetime.Terminate(Id, HasExited);
        else
            lifetime.Terminate(_managed!);
    }
    internal static MediaChildProcess Start(ProcessStartInfo start, NativeFileLease? lease)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (lease is null)
                throw new InvalidOperationException("POSIX children require an inherited native admission lease.");
            return new(PosixMediaChild.Start(start, lease));
        }
        var managed = new Process { StartInfo = start };
        try
        {
            if (!managed.Start())
                throw new IOException("The media child did not start.");
            return new(managed);
        }
        catch { managed.Dispose(); throw; }
    }
    public void Dispose() { _native?.Dispose(); _managed?.Dispose(); }
}
