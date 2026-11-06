using System.Diagnostics;

namespace Romd.Storage.FileSystem;

/// <summary>
/// Serializes cooperating processes on local filesystems supporting exclusive file sharing.
/// The lock file is permanent: unlinking it would allow two writers to lock different inodes.
/// Process termination closes the handle and releases the lease without stale-lock recovery.
/// </summary>
internal static class MutationLease
{
    internal const string FileName = ".romd-mutation.lock";

    internal static async ValueTask<FileStream> AcquireAsync(string root, CancellationToken ct)
    {
        string path = Path.Combine(root, FileName);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                ContentStoreDiagnostics.MutationWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                return lease;
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Windows sharing/lock violations and Unix EWOULDBLOCK (11 on Linux, 35 on macOS).
    /// Permission, disk, and filesystem errors must not be mistaken for a held lock.
    /// </summary>
    internal static bool IsLockContention(IOException ex) =>
        (ex.HResult & 0xffff) is 11 or 32 or 33 ||
        (OperatingSystem.IsMacOS() && (ex.HResult & 0xffff) == 35);
}
