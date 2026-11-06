using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Romd.Infrastructure.Storage;

internal sealed class MediaWorkerCleanupException(string message) : IOException(message);

/// <summary>Reaps only this child's Unix group; Windows descendants share an owned kill-on-close job.</summary>
internal sealed class MediaWorkerProcessLifetime : IDisposable
{
    private static readonly object SubreaperGate = new();
    private static bool _subreaper;
    private readonly SafeFileHandle? _job;
    private readonly Func<int, CancellationToken, Task>? _observeStop;
    public string? JobName { get; }

    public MediaWorkerProcessLifetime(Func<int, CancellationToken, Task>? observeStop = null)
    {
        _observeStop = observeStop;
        if (OperatingSystem.IsLinux())
        {
            lock (SubreaperGate)
            {
                if (!_subreaper)
                {
                    if (Prctl(36, 1, 0, 0, 0) != 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    _subreaper = true;
                }
            }
        }
        if (OperatingSystem.IsWindows())
        {
            JobName = "RomdMedia-" + Guid.NewGuid().ToString("N");
            _job = CreateJobObject(0, JobName);
            if (_job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                _job.Dispose();
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    public void Terminate(Process process)
    {
        try
        {
            if (_job is not null && !TerminateJobObject(_job, 137))
                throw new MediaWorkerCleanupException("Could not terminate the owned media process job.");
            if (!OperatingSystem.IsWindows())
            {
                int result = Kill(-process.Id, 9);
                int error = Marshal.GetLastPInvokeError();
                if (result != 0 && error != 3)
                    throw new MediaWorkerCleanupException("Could not terminate the media worker session.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    internal void Terminate(int processId, bool leaderExited)
    {
        int result = Kill(-processId, 9);
        if (result != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new MediaWorkerCleanupException("Could not terminate the owned media process group.");
        if (!leaderExited && Kill(processId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new MediaWorkerCleanupException("Could not terminate the unready media child.");
    }

    internal static bool IsWindowsJobStopped(string? name)
    {
        if (name is null || !name.StartsWith("RomdMedia-", StringComparison.Ordinal) || name.Length != 41)
            return false;
        using var job = OpenJobObject(4, false, name);
        if (job.IsInvalid)
            return Marshal.GetLastPInvokeError() == 2; // The owned named job has ceased to exist.
        return QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), out _) && accounting.ActiveProcesses == 0;
    }

    /// <summary>Recognizes a finished Linux child adopted by this process; it is still subject to group join.</summary>
    internal static bool IsAdoptedZombie(MediaProcessIdentity expected)
    {
        if (!OperatingSystem.IsLinux() || expected.Platform != "linux" || expected.ProcessId <= 1 ||
            expected.GroupId != expected.ProcessId)
            return false;
        // One stat snapshot binds the exited state and parent to the recorded birth/group.
        // A zombie cannot execute or change namespace; only its wait owner can reap it.
        string stat = File.ReadAllText($"/proc/{expected.ProcessId}/stat");
        int start = stat.IndexOf('('), end = stat.LastIndexOf(')');
        if (start < 2 || end < start || end + 2 >= stat.Length ||
            !int.TryParse(stat.AsSpan(0, start - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
            pid != expected.ProcessId)
            return false;
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 20 && fields[0] == "Z" &&
            int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) && parent == Environment.ProcessId &&
            int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int group) && group == expected.GroupId &&
            fields[19] == expected.Birth &&
            File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() == expected.Boot &&
            new FileInfo($"/proc/{expected.ProcessId}/ns/pid").LinkTarget == expected.PidNamespace;
    }

    public async Task JoinDescendantsAsync(int groupId, CancellationToken ct)
    {
        if (_observeStop is not null)
            await _observeStop(groupId, ct);
        // Linux recovery may also join a positively identified, adopted zombie leader here.
        long began = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (OperatingSystem.IsLinux())
            {
                while (WaitPid(-groupId, out _, 1) > 0)
                { }
            }
            bool alive;
            if (_job is not null)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), out _))
                    throw new MediaWorkerCleanupException("Could not query the owned media process job.");
                alive = accounting.ActiveProcesses != 0;
            }
            else
            {
                int result = Kill(-groupId, 0);
                int error = Marshal.GetLastPInvokeError();
                // Darwin may briefly report EPERM for a killed, reparented
                // descendant. Keep waiting; only ESRCH proves extinction.
                bool awaitingDarwinReap = OperatingSystem.IsMacOS() && result != 0 && error == 1;
                if (result != 0 && error != 3 && !awaitingDarwinReap)
                    throw new MediaWorkerCleanupException($"Could not confirm the media worker session stopped (errno {error}).");
                alive = result == 0 || awaitingDarwinReap;
            }
            if (!alive)
                return;
            if (Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(5))
                throw new MediaWorkerCleanupException("The media worker descendants could not be joined.");
            await Task.Delay(10, ct);
        }
    }
    public void Dispose() => _job?.Dispose();

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] private static extern int Prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int process, int signal);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)] private static extern int WaitPid(int process, out int status, int options);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string name);
    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out BasicAccounting information, uint length, out uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime; public long JobTime; public uint Flags;
        public nuint MinimumWorkingSet; public nuint MaximumWorkingSet; public uint ActiveProcesses;
        public nuint Affinity; public uint Priority; public uint Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations; public ulong WriteOperations; public ulong OtherOperations; public ulong ReadBytes; public ulong WriteBytes; public ulong OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public nuint ProcessMemory; public nuint JobMemory; public nuint PeakProcessMemory; public nuint PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long UserTime; public long KernelTime; public long PeriodUserTime; public long PeriodKernelTime;
        public uint PageFaults; public uint TotalProcesses; public uint ActiveProcesses; public uint TerminatedProcesses;
    }
}
