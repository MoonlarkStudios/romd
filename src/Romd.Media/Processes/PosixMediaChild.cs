using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Romd.Media.Processes;

/// <summary>Native spawn owns direct-child wait status and explicitly inherited descriptors.</summary>
internal sealed class PosixMediaChild : IDisposable
{
    private readonly Process _observer;
    private readonly DirectChildWait _wait;
    private readonly Task<int> _exit;
    internal int Id { get; }
    internal Process Observer => _observer;
    internal Stream Input { get; }
    internal Stream Output { get; }
    internal Stream Error { get; }
    internal Stream Ready { get; }
    internal bool HasExited => _exit.IsCompletedSuccessfully;
    internal int ExitCode => _exit.GetAwaiter().GetResult();
    internal long CpuMicroseconds => _wait.CpuMicroseconds;
    internal int TerminatingSignal => _wait.TerminatingSignal;
    internal Task WaitAsync(CancellationToken ct) => _exit.WaitAsync(ct);

    // A synchronous bounded observation must not call wait/terminate or reenter this helper.
    internal bool TryObserveOwned<T>(Func<int, T> observation, out T facts) =>
        _wait.TryObserveOwned(observation, out facts);

    /// <summary>Starts a tool whose sealed images the child inherits at descriptors 4, 5, ... in order.</summary>
    /// <remarks>Descriptor 3 is the admission lease; every other inherited descriptor is closed in the child.</remarks>
    internal static PosixMediaChild StartTool(ProcessStartInfo start, int admissionDescriptor,
        IReadOnlyList<SealedExecutable> images, Func<SafeFileHandle, FileAccess, Stream>? createStream = null)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Tool ownership uses the POSIX boundary.");
        ArgumentNullException.ThrowIfNull(images);
        return StartCore(start, admissionDescriptor, includeReady: false, images, createStream);
    }

    internal Task TerminateAndWaitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _wait.Terminate();
        return _exit.WaitAsync(ct);
    }

    private PosixMediaChild(int pid, Stream input, Stream output, Stream error, Stream ready, Process observer, DirectChildWait wait)
    {
        Id = pid;
        // Process is an observer for CPU accounting/setup hooks, never the wait owner.
        _observer = observer;
        _wait = wait;
        Input = input;
        Output = output;
        Error = error;
        Ready = ready;
        _exit = ReapAsync(wait);
    }

    internal static PosixMediaChild Start(ProcessStartInfo start, NativeFileLease lease, Func<SafeFileHandle, FileAccess, Stream>? createStream = null) =>
        StartCore(start, lease.Descriptor, includeReady: true, [], createStream);

    private const int FirstImageDescriptor = 4;

    private static PosixMediaChild StartCore(ProcessStartInfo start, int admissionDescriptor, bool includeReady,
        IReadOnlyList<SealedExecutable> images, Func<SafeFileHandle, FileAccess, Stream>? createStream)
    {
        var descriptors = new List<int>();
        var pairs = new List<int[]>();
        nint actions = 0;
        nint attributes = 0;
        var allocations = new List<nint>();
        var strings = new List<nint>();
        bool mac = OperatingSystem.IsMacOS();
        bool actionsInitialized = false;
        bool attributesInitialized = false;
        try
        {
            for (int index = 0; index < (includeReady ? 4 : 3); index++)
            {
                int[] pair = new int[2];
                Check(Pipe(pair));
                try
                {
                    // Originals are moved above the fixed child targets, avoiding descriptor collisions.
                    for (int end = 0; end < 2; end++)
                    {
                        int relocated = NativeFileLease.Fcntl(pair[end], mac ? 67 : 1030, 10);
                        if (relocated < 0)
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                        descriptors.Add(relocated);
                    }
                    pairs.Add([descriptors[^2], descriptors[^1]]);
                }
                finally { NativeFileLease.Close(pair[0]); NativeFileLease.Close(pair[1]); }
            }
            if (!mac)
            {
                // glibc keeps these opaque structures inline; Darwin uses pointer handles.
                actions = Marshal.AllocHGlobal(1024);
                attributes = Marshal.AllocHGlobal(1024);
                Marshal.Copy(new byte[1024], 0, actions, 1024);
                Marshal.Copy(new byte[1024], 0, attributes, 1024);
            }
            Check(mac ? MacActionsInit(ref actions) : ActionsInit(actions));
            actionsInitialized = true;
            Check(mac ? MacAttributesInit(ref attributes) : AttributesInit(attributes));
            attributesInitialized = true;
            if (mac)
                Check(MacSetFlags(ref attributes, 0x4000)); // CLOEXEC_DEFAULT: actions explicitly whitelist descriptors.
            AddDuplicate(pairs[0][0], 0);
            AddDuplicate(pairs[1][1], 1);
            AddDuplicate(pairs[2][1], 2);
            int inheritedLease = NativeFileLease.Fcntl(admissionDescriptor, mac ? 67 : 1030, 10);
            if (inheritedLease < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            descriptors.Add(inheritedLease);
            AddDuplicate(inheritedLease, 3);
            if (includeReady && images.Count > 0)
                throw new ArgumentException("Supervised workers inherit no tool images.", nameof(images));
            if (includeReady)
                AddDuplicate(pairs[3][1], 4);
            for (int index = 0; index < images.Count; index++)
            {
                if (mac)
                    throw new PlatformNotSupportedException("Sealed tool images require Linux.");
                int image = NativeFileLease.Fcntl(images[index].Descriptor, 1030, 10);
                if (image < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                descriptors.Add(image);
                AddDuplicate(image, FirstImageDescriptor + index); // dup2 clears close-on-exec for the child only.
            }
            if (!mac)
                Check(CloseFrom(actions, includeReady ? 5 : FirstImageDescriptor + images.Count));
            else
                foreach (int descriptor in descriptors)
                    Check(MacAddClose(ref actions, descriptor));
            var argv = new[] { start.FileName }.Concat(start.ArgumentList).ToArray();
            var environment = start.Environment.Select(pair => pair.Key + "=" + pair.Value).ToArray();
            nint arguments = Array(argv);
            nint env = Array(environment);
            int result = mac ? MacSpawn(out int pid, start.FileName, ref actions, ref attributes, arguments, env)
                : Spawn(out pid, start.FileName, actions, attributes, arguments, env);
            Check(result);
            var wait = new DirectChildWait(pid);
            int[] retained = includeReady ? [pairs[0][1], pairs[1][0], pairs[2][0], pairs[3][0]]
                : [pairs[0][1], pairs[1][0], pairs[2][0]];
            PosixMediaChild child;
            Process? observer = null;
            var streams = new List<Stream>();
            try
            {
                observer = Process.GetProcessById(pid);
                var access = new[] { FileAccess.Write, FileAccess.Read, FileAccess.Read, FileAccess.Read };
                for (int index = 0; index < retained.Length; index++)
                {
                    var owner = new SafeFileHandle(retained[index], ownsHandle: true);
                    descriptors.Remove(retained[index]); // Ownership transfers once, before stream construction.
                    try
                    { streams.Add((createStream ?? Stream)(owner, access[index])); }
                    catch { owner.Dispose(); throw; }
                }
                child = new(pid, streams[0], streams[1], streams[2], includeReady ? streams[3] : System.IO.Stream.Null, observer, wait);
            }
            catch (Exception failure)
            {
                // No asynchronous reaper exists until all streams have transferred successfully.
                // Use that same ownership guard if construction fails before returning a child.
                var failures = new List<Exception> { failure };
                try
                { wait.Terminate(); }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
                try
                { while (!wait.TryReap(0, out _)) { } }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
                foreach (var stream in streams)
                {
                    try
                    { stream.Dispose(); }
                    catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
                }
                try
                { observer?.Dispose(); }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
                if (failures.Count > 1)
                    throw new AggregateException("Native child setup and cleanup failed.", failures);
                throw;
            }
            return child;

            void AddDuplicate(int from, int to) => Check(mac ? MacAddDuplicate(ref actions, from, to) : AddDuplicateNative(actions, from, to));
            nint Array(string[] values)
            {
                nint pointer = Marshal.AllocHGlobal((values.Length + 1) * nint.Size);
                allocations.Add(pointer);
                for (int index = 0; index < values.Length; index++)
                {
                    nint value = Marshal.StringToCoTaskMemUTF8(values[index]);
                    strings.Add(value);
                    Marshal.WriteIntPtr(pointer, index * nint.Size, value);
                }
                Marshal.WriteIntPtr(pointer, values.Length * nint.Size, 0);
                return pointer;
            }
        }
        finally
        {
            if (actionsInitialized)
            { if (mac) MacActionsDestroy(ref actions); else ActionsDestroy(actions); }
            if (attributesInitialized)
            { if (mac) MacAttributesDestroy(ref attributes); else AttributesDestroy(attributes); }
            if (!mac)
            { if (actions != 0) Marshal.FreeHGlobal(actions); if (attributes != 0) Marshal.FreeHGlobal(attributes); }
            foreach (nint allocation in allocations)
                Marshal.FreeHGlobal(allocation);
            foreach (nint value in strings)
                Marshal.FreeCoTaskMem(value);
            foreach (int descriptor in descriptors)
                NativeFileLease.Close(descriptor);
        }
    }

    private static Stream Stream(SafeFileHandle descriptor, FileAccess access) => new FileStream(descriptor, access, 4096, isAsync: false);
    private static void Check(int result) { if (result != 0) throw new Win32Exception(result == -1 ? Marshal.GetLastPInvokeError() : result); }
    private static async Task<int> ReapAsync(DirectChildWait wait)
    {
        while (true)
        {
            if (wait.TryReap(1, out int exitCode))
                return exitCode;
            await Task.Delay(10);
        }
    }

    private sealed class DirectChildWait(int pid)
    {
        private readonly object _guard = new();
        private bool _ownsPid = true;
        internal long CpuMicroseconds { get; private set; }
        internal int TerminatingSignal { get; private set; }

        internal bool TryObserveOwned<T>(Func<int, T> observation, out T facts)
        {
            lock (_guard)
            {
                if (!_ownsPid)
                { facts = default!; return false; }
                facts = observation(pid);
                return true;
            }
        }

        internal void Terminate()
        {
            lock (_guard)
            {
                if (!_ownsPid)
                    return;
                if (Kill(pid, 9) != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 3)
                        throw new Win32Exception(error); // ESRCH can precede collecting the child's status.
                }
            }
        }

        internal bool TryReap(int options, out int exitCode)
        {
            lock (_guard)
            {
                LinuxResourceUsage usage = default;
                long? finalCpu = null;
                int result, status = 0;
                if (OperatingSystem.IsLinux())
                {
                    // Keep the exited child owned and unreaped while sampling the same PROF clock
                    // RLIMIT_CPU uses. wait4's adjusted rusage can be lower than that limit clock.
                    result = WaitId(1, (uint)pid, out LinuxChildInfo info, options | 4 | 0x01000000); // P_PID, WEXITED, WNOWAIT
                    if (result == 0 && info.ProcessId != pid)
                    { exitCode = 0; return false; }
                    if (result == 0)
                    {
                        if (ClockGetTime(unchecked(~pid << 3), out LinuxTimespec cpu) == 0 &&
                            cpu.Seconds is >= 0 and < long.MaxValue / 1_000_000 && cpu.Nanoseconds is >= 0 and < 1_000_000_000)
                            finalCpu = cpu.Seconds * 1_000_000 + cpu.Nanoseconds / 1000;
                        result = Wait4(pid, out status, options, out usage);
                    }
                }
                else
                    result = WaitPid(pid, out status, options);
                if (result == pid)
                {
                    // Retire ownership before the caller or its Task can publish completion.
                    _ownsPid = false;
                    if (OperatingSystem.IsLinux())
                        CpuMicroseconds = finalCpu ?? checked(
                        (usage.UserSeconds + usage.SystemSeconds) * 1_000_000 + usage.UserMicroseconds + usage.SystemMicroseconds);
                    TerminatingSignal = status & 0x7f;
                    exitCode = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                    return true;
                }
                if (result < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 4)
                    {
                        _ownsPid = false;
                        throw new Win32Exception(error);
                    }
                }
                exitCode = 0;
                return false;
            }
        }
    }
    public void Dispose() { Input.Dispose(); Output.Dispose(); Error.Dispose(); Ready.Dispose(); _observer.Dispose(); }

    // Linux 64-bit siginfo_t: the SIGCHLD union starts at byte 16 on x64 and ARM64.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct LinuxChildInfo { [FieldOffset(16)] public int ProcessId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec { public long Seconds, Nanoseconds; }
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)]
    private static extern int WaitId(int idType, uint id, out LinuxChildInfo info, int options);
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out LinuxTimespec time);

    // Linux 64-bit rusage: two timeval pairs followed by fourteen native-long counters.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxResourceUsage
    {
        public long UserSeconds, UserMicroseconds, SystemSeconds, SystemMicroseconds;
        public long MaxRss, IxRss, IdRss, IsRss, MinorFaults, MajorFaults, Swaps, InputBlocks, OutputBlocks;
        public long MessagesSent, MessagesReceived, Signals, VoluntarySwitches, InvoluntarySwitches;
    }
    [DllImport("libc", EntryPoint = "wait4", SetLastError = true)]
    private static extern int Wait4(int pid, out int status, int options, out LinuxResourceUsage usage);
    [DllImport("libc", EntryPoint = "pipe", SetLastError = true)] private static extern int Pipe([Out] int[] pair);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)] private static extern int WaitPid(int pid, out int status, int options);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = true)] private static extern int Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attributes, nint argv, nint env);
    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = true)] private static extern int MacSpawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref nint actions, ref nint attributes, nint argv, nint env);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")] private static extern int ActionsInit(nint actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")] private static extern int MacActionsInit(ref nint actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")] private static extern int ActionsDestroy(nint actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")] private static extern int MacActionsDestroy(ref nint actions);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")] private static extern int AttributesInit(nint attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")] private static extern int MacAttributesInit(ref nint attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")] private static extern int AttributesDestroy(nint attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")] private static extern int MacAttributesDestroy(ref nint attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")] private static extern int MacSetFlags(ref nint attributes, short flags);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")] private static extern int AddDuplicateNative(nint actions, int from, int to);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")] private static extern int MacAddDuplicate(ref nint actions, int from, int to);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")] private static extern int MacAddClose(ref nint actions, int descriptor);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")] private static extern int CloseFrom(nint actions, int first);
}
