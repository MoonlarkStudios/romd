using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Romd.Infrastructure.Storage;

internal enum MediaProcessPresence { Known, Gone, Unknown }
internal sealed record MediaProcessObservation(MediaProcessPresence Presence, MediaProcessIdentity? Identity = null);

internal sealed record MediaProcessIdentity(int ProcessId, int GroupId, string Platform, string Boot, string Birth, string PidNamespace)
{
    internal static MediaProcessObservation Observe(int pid)
    {
        var identity = Read(pid);
        if (identity is not null)
            return new(MediaProcessPresence.Known, identity);
        // Unreadability (including hidepid/permission failures) is not disappearance.
        // ESRCH independently establishes absence in this observer's PID namespace.
        if (!OperatingSystem.IsWindows())
            return Kill(pid, 0) < 0 && Marshal.GetLastPInvokeError() == 3
                ? new(MediaProcessPresence.Gone) : new(MediaProcessPresence.Unknown);
        try
        { using var process = Process.GetProcessById(pid); return new(MediaProcessPresence.Unknown); }
        catch (ArgumentException) { return new(MediaProcessPresence.Gone); }
        catch (System.ComponentModel.Win32Exception) { return new(MediaProcessPresence.Unknown); }
    }
    internal static MediaProcessIdentity? Read(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (ParseLinuxStat(File.ReadAllText($"/proc/{pid}/stat")) is not { } stat)
                    return null;
                string? pidNamespace = new FileInfo($"/proc/{pid}/ns/pid").LinkTarget;
                if (!ValidNamespace("linux", pidNamespace))
                    return null;
                return new(pid, stat.Group, "linux", File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), stat.Birth, pidNamespace!);
            }
            if (OperatingSystem.IsMacOS())
            {
                byte[] info = new byte[136];
                if (ProcInfo(pid, 3, 0, info, info.Length) != info.Length || BitConverter.ToInt32(info, 12) != pid)
                    return null;
                byte[] boot = new byte[16];
                nuint length = (nuint)boot.Length;
                if (Sysctl("kern.boottime", boot, ref length, 0, 0) != 0 || length != 16)
                    return null;
                return new(pid, BitConverter.ToInt32(info, 100), "darwin",
                    Pair(BitConverter.ToUInt64(boot, 0), BitConverter.ToUInt64(boot, 8)),
                    Pair(BitConverter.ToUInt64(info, 120), BitConverter.ToUInt64(info, 128)), "host");
            }
            using var process = Process.GetProcessById(pid);
            return new(pid, 0, "windows", "job-object", process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), "host");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return null; }
    }
    /// <summary>This PID namespace's init. A host init owned by another user hides its namespace link; then a single
    /// NSpid entry for this process proves procfs belongs to this namespace, so procfs PID 1 is this namespace's init.
    /// Every other shape stays unknown.</summary>
    internal static MediaProcessIdentity? ReadNamespaceInit()
    {
        if (Read(1) is { } init)
            return init;
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            if (Read(Environment.ProcessId) is not { } here ||
                !IsSolePidNamespaceMember(File.ReadAllText("/proc/self/status"), Environment.ProcessId) ||
                ParseLinuxStat(File.ReadAllText("/proc/1/stat")) is not { } stat)
                return null;
            return new(1, stat.Group, "linux", here.Boot, stat.Birth, here.PidNamespace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }
    internal static bool IsSolePidNamespaceMember(string status, int pid)
    {
        string[] lines = status.Split('\n').Where(line => line.StartsWith("NSpid:", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1)
            return false;
        string[] values = lines[0]["NSpid:".Length..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return values.Length == 1 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value == pid;
    }
    private static (int Group, string Birth)? ParseLinuxStat(string stat)
    {
        int end = stat.LastIndexOf(')');
        if (end < 0)
            return null;
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 20 && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int group) &&
            ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? (group, fields[19]) : null;
    }
    internal static bool ValidNamespace(string platform, string? value) => platform == "linux"
        ? value is { Length: > 6 and <= 128 } && value.StartsWith("pid:[", StringComparison.Ordinal) && value.EndsWith(']') &&
            ulong.TryParse(value.AsSpan(5, value.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) && number > 0
        : value == "host";
    private static string Pair(ulong first, ulong second) => FormattableString.Invariant($"{first}:{second}");
    [DllImport("libproc", EntryPoint = "proc_pidinfo", SetLastError = true)] private static extern int ProcInfo(int pid, int flavor, ulong argument, [Out] byte[] buffer, int size);
    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)] private static extern int Sysctl([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] value, ref nuint length, nint replacement, nuint replacementLength);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
}
