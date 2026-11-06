using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Romd.Mount.Mounting;

internal interface IMountRecoveryHost
{
    Guid BootSession { get; }
    bool ProcessMatches(int pid, DateTime startedUtc);
    MacMountIdentity? FindMount(string path);
    Task UnmountAsync(string path, MacMountIdentity expected, CancellationToken ct);
    Task StopChildAsync(int pid, DateTime startedUtc, string executable, CancellationToken ct, bool allowLauncher = false);
    void ClearBrokerDirectory(MacSocketDirectoryIdentity directory);
}

internal static class MountRecovery
{
    // Caller holds both leases. Never invoke while an active supervisor owns them.
    internal static async Task<bool> RecoverAsync(string directory, string key, IMountRecoveryHost host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reservation = MountReservation.Read(directory, key);
        if (reservation is null) return false;
        var record = reservation.State;
        bool sameBoot = record.BootSession == host.BootSession;
        if (sameBoot && host.ProcessMatches(record.OwnerPid, record.OwnerStartedUtc))
            throw new MountPrerequisiteException("The recorded supervisor is still running. Stop it normally before recovery.");
        var mounted = host.FindMount(record.Mountpoint);
        if (!sameBoot)
        {
            // Old processes cannot survive a reboot. Never act on a new mount that
            // happens to occupy the old pathname, even if its filesystem ID repeats.
            if (mounted is not null) throw new MountPrerequisiteException("A mount occupies the old path after reboot; reservation preserved.");
        }
        else
        {
            if (record.Backend != "nfs-development")
                throw new MountPrerequisiteException("Recovery requires a recorded NFS child. Incomplete startup or unsupported backend evidence was preserved.");
            var identity = record.MountIdentity;
            if (record.ChildPid is null && (!record.LaunchGated || identity is not null))
                throw new MountPrerequisiteException("Startup lacks child identity and a launch gate; reservation preserved.");
            if (identity is not null && identity is not { ReadOnly: true, FileSystem: "nfs" })
                throw new MountPrerequisiteException("The recorded filesystem is not read-only NFS; reservation preserved.");
            if (mounted is not null)
            {
                if (identity is null || mounted != identity) throw new MountPrerequisiteException("The current mount lacks matching recorded identity; no mount or process was changed.");
                await host.UnmountAsync(record.Mountpoint, identity, ct);
            }
            // A recorded child may have stalled before mounting. Stop only that
            // verified process, then recheck: it could mount between these checks.
            if (host.FindMount(record.Mountpoint) is not null) throw new IOException("Unmount is unconfirmed; reservation preserved.");
            if (record.ChildPid is { } child && record.ChildStartedUtc is { } started)
            {
                await host.StopChildAsync(child, started, record.Executable, ct, record.LaunchGated && identity is null);
                if (host.ProcessMatches(child, started)) throw new IOException("Mount child is still running; reservation preserved.");
            }
            // Without a recorded child, a gated launcher never received permission
            // to exec. Its input closes on supervisor death; do not guess its PID.
        }
        ct.ThrowIfCancellationRequested();
        if (host.FindMount(record.Mountpoint) is not null) throw new IOException("Mount state changed; reservation preserved.");
        if (record.BrokerDirectory is not null) host.ClearBrokerDirectory(record.BrokerDirectory);
        reservation.ClearRecovered(directory, key);
        // Preserve mountpoint and cache directories; neither is needed to clear ownership.
        return true;
    }
}

internal sealed class MacMountRecoveryHost : IMountRecoveryHost
{
    public void ClearBrokerDirectory(MacSocketDirectoryIdentity directory) => MacDirectoryCleanup.ClearSocketDirectory(directory);
    public Guid BootSession => MacMountTable.BootSession();
    public MacMountIdentity? FindMount(string path) => MacMountTable.Find(path);
    public bool ProcessMatches(int pid, DateTime startedUtc)
    {
        using var process = MatchingProcess(pid, startedUtc);
        return process is not null;
    }

    public async Task UnmountAsync(string path, MacMountIdentity expected, CancellationToken ct)
    {
        foreach (string executable in new[] { "/sbin/umount", "/usr/sbin/diskutil" })
        {
            ct.ThrowIfCancellationRequested();
            var current = FindMount(path);
            if (current is null) return;
            if (current != expected || !current.ReadOnly || current.FileSystem != "nfs")
                throw new IOException("Mount identity changed before unmount; reservation preserved.");
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (executable.EndsWith("diskutil", StringComparison.Ordinal))
            { start.ArgumentList.Add("unmount"); start.ArgumentList.Add("force"); }
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                ct.ThrowIfCancellationRequested();
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (FindMount(path) is not null) throw new IOException("Cannot unmount the recorded filesystem; reservation preserved.");
    }

    public async Task StopChildAsync(int pid, DateTime startedUtc, string executable, CancellationToken ct, bool allowLauncher = false)
    {
        ct.ThrowIfCancellationRequested();
        using var process = MatchingProcess(pid, startedUtc);
        if (process is null) return;
        string actual = process.MainModule?.FileName ?? throw new IOException("Cannot verify the remaining child executable.");
        string actualPath = Canonical(actual);
        if (actualPath != Canonical(executable) && (!allowLauncher || actualPath != Canonical(GatedMountLaunch.Launcher)))
            throw new IOException("Child executable differs from reservation.");
        if (process.HasExited) return;
        if (process.StartTime.ToUniversalTime() != startedUtc) throw new IOException("Child process identity changed.");
        process.Kill(); // Never kill an unverified process tree.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private static Process? MatchingProcess(int pid, DateTime startedUtc)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime() == startedUtc) return process;
            process.Dispose();
            return null;
        }
        catch { process.Dispose(); throw; }
    }

    private static string Canonical(string path)
    {
        nint value = realpath(path, 0);
        if (value == 0) throw new IOException("Cannot verify executable path.");
        try { return Marshal.PtrToStringUTF8(value) ?? throw new IOException("Invalid executable path."); }
        finally { free(value); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern nint realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern void free(nint value);
}
