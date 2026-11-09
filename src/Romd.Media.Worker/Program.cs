using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;

return await RunAsync(args, CancellationToken.None);

static async Task<int> RunAsync(string[] arguments, CancellationToken ct)
{
    if (arguments.Length < 5 || arguments[^4] != "--run" || arguments[^2] != "--nonce" ||
        !Path.IsPathFullyQualified(arguments[^3]) || !Guid.TryParseExact(arguments[^1], "N", out _))
        return 64;
    string runPath = arguments[^3];
    string nonce = arguments[^1];
    arguments = arguments[..^4];
    string? job = null;
    if (OperatingSystem.IsWindows())
    {
        if (arguments.Length < 3 || arguments[^2] != "--job" || !NativeJob.Join(arguments[^1]))
            return 64;
        job = arguments[^1];
        arguments = arguments[..^2];
    }
    if (arguments.Length is not (1 or 4 or 8 or 9) || arguments[0] is not ("handshake" or "handshake-chd" or "inspect-rvz" or "inspect-iso" or "inspect-chd" or "inspect-chd-cd" or "encode" or "encode-chd" or "encode-chd-cd" or "verify-chd-cd" or "reverify-chd-cd" or "extract-chd-dvd") ||
        arguments.Length != (arguments[0] == "extract-chd-dvd" ? 9 : arguments[0] is "encode" or "encode-chd" or "encode-chd-cd" ? 8 : arguments[0] is "handshake" or "handshake-chd" ? 1 : 4))
        return 64;
    int session = OperatingSystem.IsWindows() ? 0 : NativeSession.Create();
    if (!OperatingSystem.IsWindows() && session != Environment.ProcessId)
        return 64;
    MediaWorkerLimits? limits = null;
    if (OperatingSystem.IsLinux())
    {
        if (!NativeSession.TryLimits(out limits))
            return 64;
    }
    var configuration = GC.GetConfigurationVariables();
    long heap = configuration.TryGetValue("GCHeapHardLimit", out var configuredHeap) ? Convert.ToInt64(configuredHeap, CultureInfo.InvariantCulture) : 0;
    long range = configuration.TryGetValue("GCRegionRange", out var configuredRange) ? Convert.ToInt64(configuredRange, CultureInfo.InvariantCulture) : 0;
    if (heap <= 0)
        return 64;
    var identity = new MediaWorkerIdentity(Environment.ProcessId, session, heap,
        MediaInspectionService.DecoderBuild, "romd-media-worker/mvid:" + typeof(NativeSession).Module.ModuleVersionId, limits,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, range, job);
    if (!await NativeRunGuard.WaitForPermitAsync(runPath, nonce, identity, ct, arguments[0] is "encode-chd-cd" or "verify-chd-cd" or "reverify-chd-cd"))
        return 64;
    MediaWorkerReply reply;
    if (arguments[0] == "handshake" && arguments.Length == 1)
        reply = new(MediaWorkerStatus.Ready, identity);
    else if (arguments[0] is "handshake-chd" or "inspect-chd" or "inspect-chd-cd" or "verify-chd-cd" or "reverify-chd-cd")
    {
        // Explicit CHD inputs are read only after the ordinary durable run permit.
        // The parent's verified identity is re-verified here; a record changed after the parent's check is refused.
        string? directory = Environment.GetEnvironmentVariable("ROMD_CHD_NATIVE_DIRECTORY");
        string? expectedJson = Environment.GetEnvironmentVariable("ROMD_CHD_NATIVE_IDENTITY");
        if (string.IsNullOrWhiteSpace(directory) ||
            expectedJson is null || expectedJson.Length > MediaWorkerProtocol.MaximumNativeIdentityBytes)
            return 64;
        ChdNativeCandidate candidate;
        try
        {
            using var document = JsonDocument.Parse(expectedJson, new JsonDocumentOptions { MaxDepth = 3 });
            if (!MediaWorkerProtocol.TryReadNativeIdentity(document.RootElement, out ChdNativeIdentity? expected) || expected is null)
                return 64;
            candidate = new(directory, expected);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return 64; }
        if (arguments[0] == "handshake-chd")
        {
            try
            { reply = new(MediaWorkerStatus.Ready, identity, NativeIdentity: candidate.VerifyAndLoad(ct)); }
            catch (ChdNativeCandidateException error) { reply = new(error.Status, identity, ChdRejection: error.Rejection); }
        }
        else
        {
            if (!Path.IsPathFullyQualified(arguments[1]) ||
                !long.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out long artifactLimit) || artifactLimit is < 4 or > (16L << 30) ||
                !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out long logicalLimit) || logicalLimit is < 0x81 or > (16L << 30))
                return 64;
            if (arguments[0] is "verify-chd-cd" or "reverify-chd-cd")
            {
                string? requestPath = Environment.GetEnvironmentVariable("ROMD_CD_REQUEST_PATH");
                string? requestHash = Environment.GetEnvironmentVariable("ROMD_CD_REQUEST_SHA256");
                if (requestPath is null || requestHash is null || !long.TryParse(Environment.GetEnvironmentVariable("ROMD_CD_REQUEST_BYTES"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long requestBytes))
                    return 64;
                var result = arguments[0] == "reverify-chd-cd"
                    ? await ChdCdRetainedVerificationService.VerifyAsync(arguments[1], requestPath, requestBytes, requestHash, candidate, artifactLimit, logicalLimit, ct)
                    : await ChdCdVerificationService.VerifyAsync(arguments[1], requestPath, requestBytes, requestHash, candidate, artifactLimit, logicalLimit, ct);
                reply = new(result.Status, identity, NativeIdentity: result.Native, CdInspection: result.Inspection,
                    CdVerification: result.Verification, CdFailure: result.Failure);
            }
            else if (arguments[0] == "inspect-chd-cd")
            {
                var result = await ChdCdInspectionService.InspectAsync(arguments[1], candidate, artifactLimit, logicalLimit, ct);
                reply = new(result.Status, identity, NativeIdentity: result.NativeIdentity,
                    ChdRejection: result.Rejection, CdInspection: result.Inspection);
            }
            else
            {
                var result = await ChdInspectionService.InspectAsync(arguments[1], candidate, artifactLimit, logicalLimit, ct);
                reply = new(result.Status, identity, result.Inspection, NativeIdentity: result.NativeIdentity,
                    ChdInspection: result.ChdInspection, ChdRejection: result.Rejection, Ps2Inspection: result.Ps2Inspection);
            }
        }
    }
    else if (arguments[0] is "encode-chd" or "extract-chd-dvd" or "encode-chd-cd")
    {
        bool extract = arguments[0] == "extract-chd-dvd";
        long expectedLogicalBytes = 0;
        if (!Path.IsPathFullyQualified(arguments[1]) || !Path.IsPathFullyQualified(arguments[2]) ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out long artifactLimit) || artifactLimit is < 4 or > (16L << 30) ||
            !long.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out long logicalLimit) || logicalLimit is <= 0x80 or > (16L << 30) ||
            !long.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out long expectedSourceBytes) || expectedSourceBytes is < 1 or > (16L << 30) ||
            !long.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out long outputLimit) || outputLimit is < 124 or > (16L << 30) ||
            arguments[7].Length != 64 || arguments[7].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !arguments[7].Any(c => c != '0') || extract &&
                (!long.TryParse(arguments[8], NumberStyles.None, CultureInfo.InvariantCulture, out expectedLogicalBytes) ||
                 expectedLogicalBytes is < 2048 or > ChdDvdLimits.MaximumLogicalBytes || expectedLogicalBytes % 2048 != 0))
            return 64;
        // Candidate construction performs no I/O. The durable permit is already held;
        // fd3 is borrowed by the tool launcher and remains rooted until worker exit.
        string? directory = Environment.GetEnvironmentVariable("ROMD_CHDMAN_DIRECTORY");
        string? expectedJson = Environment.GetEnvironmentVariable("ROMD_CHDMAN_IDENTITY");
        if (string.IsNullOrWhiteSpace(directory) || expectedJson is null ||
            expectedJson.Length > MediaWorkerProtocol.MaximumToolIdentityBytes ||
            Encoding.UTF8.GetByteCount(expectedJson) > MediaWorkerProtocol.MaximumToolIdentityBytes)
            return 64;
        ChdmanCandidate candidate;
        try
        {
            using var document = JsonDocument.Parse(expectedJson, new JsonDocumentOptions { MaxDepth = 3 });
            if (!MediaWorkerProtocol.TryReadToolIdentity(document.RootElement, out ChdmanToolIdentity? expected) || expected is null)
                return 64;
            candidate = new(directory, expected);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return 64; }
        if (!NativePriority.TryReduce(out string kind, out int priority))
            reply = arguments[0] == "encode-chd-cd"
                ? new(MediaWorkerStatus.IoFailure, identity, CdFailure: new(ChdCdJobCause.ToolRejected, ChdDvdEncodingCause.ToolPriorityMismatch))
                : new(MediaWorkerStatus.IoFailure, identity, ChdEncodingRejection: new(ChdDvdEncodingStage.ToolLaunch,
                    ChdDvdEncodingCause.ToolPriorityMismatch, candidate.BinaryPath));
        else if (arguments[0] == "encode-chd-cd")
        {
            var result = await ChdCdEncodingService.EncodeAsync(arguments[1], arguments[2], expectedSourceBytes, arguments[7], candidate,
                artifactLimit, logicalLimit, outputLimit, kind, priority, 3, ct);
            var execution = result.Diagnostics is { } d ? new ChdToolExecution(d.ProcessId, d.ExitCode, d.StandardOutputBytes, d.StandardErrorBytes, d.CpuMicroseconds) : null;
            reply = new(result.Status, identity, ToolIdentity: result.Tool, ToolExecution: execution, CdEncoding: result.Artifact, CdFailure: result.Failure);
        }
        else if (extract)
        {
            var result = await ChdDvdEncodingService.ExtractAsync(arguments[1], arguments[2], candidate,
                expectedSourceBytes, arguments[7], expectedLogicalBytes, artifactLimit, logicalLimit, outputLimit,
                kind, priority, admissionDescriptor: 3, ct: ct);
            var execution = result.Diagnostics is { } diagnostics
                ? new ChdToolExecution(diagnostics.ProcessId, diagnostics.ExitCode, diagnostics.StandardOutputBytes, diagnostics.StandardErrorBytes, diagnostics.CpuMicroseconds) : null;
            reply = new(result.Status, identity, ChdExtraction: result.Extraction, ToolIdentity: result.ToolIdentity,
                ChdEncodingRejection: result.Rejection, ToolExecution: execution);
        }
        else
        {
            var result = await ChdDvdEncodingService.EncodeAsync(arguments[1], arguments[2], candidate,
                expectedSourceBytes, arguments[7], artifactLimit, logicalLimit, outputLimit, kind, priority, admissionDescriptor: 3, ct: ct);
            var execution = result.Diagnostics is { } diagnostics
                ? new ChdToolExecution(diagnostics.ProcessId, diagnostics.ExitCode, diagnostics.StandardOutputBytes, diagnostics.StandardErrorBytes, diagnostics.CpuMicroseconds) : null;
            reply = new(result.Status, identity, ChdEncoding: result.Artifact, ToolIdentity: result.ToolIdentity,
                ChdEncodingRejection: result.Rejection, ToolExecution: execution);
        }
    }
    else if (arguments[0] == "encode")
    {
        if (!Path.IsPathFullyQualified(arguments[1]) || !Path.IsPathFullyQualified(arguments[2]) ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out long artifactLimit) || artifactLimit is < 4 or > (16L << 30) ||
            !long.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out long logicalLimit) || logicalLimit is <= 0x80 or > (16L << 30) ||
            !long.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out long logicalBytes) || logicalBytes is <= 0x80 || logicalBytes > logicalLimit ||
            !long.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out long outputLimit) || outputLimit is < 0x124 or > (16L << 30))
            return 64;
        NintendoDiscKind discKind;
        if (RvzCodecFamily.ForWorkerArgument(arguments[7]) is { } family)
            discKind = family.DiscKind;
        else
            return 64;
        if (!NativePriority.TryReduce(out string kind, out int priority))
            reply = new(MediaWorkerStatus.IoFailure, identity);
        else
        {
            var result = await MediaEncodingService.EncodeAsync(arguments[1], arguments[2], artifactLimit,
                logicalLimit, logicalBytes, outputLimit, discKind, kind, priority, ct);
            reply = new(result.Status, identity, Encoding: result.Encoding);
        }
    }
    else
    {
        if (arguments.Length != 4 || !Path.IsPathFullyQualified(arguments[1]) ||
            !long.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out long artifactLimit) || artifactLimit is < 4 or > (16L << 30) ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out long logicalLimit) || logicalLimit is <= 0x80 or > (16L << 30))
            return 64;
        var result = arguments[0] == "inspect-iso"
            ? await MediaInspectionService.InspectIsoAsync(arguments[1], artifactLimit, logicalLimit, ct)
            : await MediaInspectionService.InspectRvzAsync(arguments[1], artifactLimit, logicalLimit, ct);
        reply = new(result.Status, identity, result.Inspection, result.IsoInspection, Ps2Inspection: result.Ps2Inspection);
    }
    byte[] output = MediaWorkerProtocol.Write(reply);
    if (output.Length > (reply.CdEncoding is not null || reply.CdVerification is not null || reply.CdFailure is not null ? MediaWorkerProtocol.MaximumCdJobOutputBytes : reply.CdInspection is null ? MediaWorkerProtocol.MaximumOutputBytes : MediaWorkerProtocol.MaximumCdOutputBytes))
        return 64;
    await Console.OpenStandardOutput().WriteAsync(output, ct);
    return 0;
}

internal static class NativeRunGuard
{
    public static async Task<bool> WaitForPermitAsync(string path, string nonce, MediaWorkerIdentity identity, CancellationToken ct, bool cdJob = false)
    {
        try
        {
            using var record = JsonDocument.Parse(await ReadRecordAsync(path, ct, maximumBytes: cdJob ? 1 << 20 : 16384), new JsonDocumentOptions { MaxDepth = 5 });
            var root = record.RootElement;
            string[] names = ["Version", "RunId", "Slot", "MaximumConcurrent", "JobId", "Operation", "Phase", "Parent", "WorkerBuild", "WindowsJob", "Identity", "Admission", "Staging"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length ||
                root.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)) || !Unique(root) ||
                root.GetProperty("Version").GetString() != "romd-media-run/v2" || root.GetProperty("RunId").GetString() != nonce ||
                root.GetProperty("Phase").GetString() != "launching" || root.GetProperty("WorkerBuild").GetString() != identity.WorkerBuild ||
                root.GetProperty("WindowsJob").GetString() != identity.JobName ||
                !root.GetProperty("Slot").TryGetInt32(out int slot) || slot is < 0 or > 15 ||
                Path.GetFileName(path) != $"run-{slot}.json")
                return false;
            AdmissionIdentity? admission = null;
            if (!OperatingSystem.IsWindows())
            {
                admission = ReadAdmission(3);
                var expected = root.GetProperty("Admission");
                if (admission is null || expected.ValueKind != JsonValueKind.Object || expected.EnumerateObject().Count() != 3 ||
                    expected.GetProperty("Platform").GetString() != admission.Platform ||
                    expected.GetProperty("Device").GetUInt64() != admission.Device || expected.GetProperty("Inode").GetUInt64() != admission.Inode)
                    return false;
                byte[] leaseNonce = new byte[32];
                if (Pread(3, leaseNonce, 32, 0) != 32 || Encoding.ASCII.GetString(leaseNonce) != nonce || Fcntl(3, 1, 0) < 0)
                    return false;
                // A fresh description must conflict. Never unlock or close inherited fd3;
                // it is rooted until kernel exit, including every operation failure.
                int probe = Open(Path.Combine(Path.GetDirectoryName(path)!, $"processing-{slot}.lease"), 2);
                if (probe < 0)
                    return false;
                try
                {
                    if (ReadAdmission(probe) != admission || Flock(probe, 2 | 4) == 0 || Marshal.GetLastPInvokeError() is not (11 or 35))
                        return false;
                }
                finally { Close(probe); }
            }
            else if (root.GetProperty("Admission").ValueKind != JsonValueKind.Null)
                return false;
            var birth = ReadBirth();
            if (birth is null || root.GetProperty("Parent").GetProperty("Platform").GetString() != birth.Value.Platform ||
                root.GetProperty("Parent").GetProperty("Boot").GetString() != birth.Value.Boot ||
                root.GetProperty("Parent").GetProperty("PidNamespace").GetString() != birth.Value.PidNamespace)
                return false;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = "romd-media-child/v2",
                RunId = nonce,
                ProcessId = Environment.ProcessId,
                GroupId = identity.SessionId,
                birth.Value.Platform,
                birth.Value.Boot,
                birth.Value.Birth,
                birth.Value.PidNamespace,
                Admission = admission
            });
            using (var file = new FileStream(path + ".identity", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { await file.WriteAsync(bytes, ct); file.Flush(flushToDisk: true); }
            byte[] ready = Encoding.ASCII.GetBytes(nonce + "\n");
            if (OperatingSystem.IsWindows())
                await Console.OpenStandardOutput().WriteAsync(ready, ct);
            else
            {
                using var output = new FileStream(new SafeFileHandle(4, ownsHandle: false), FileAccess.Write);
                await output.WriteAsync(ready, ct);
                await output.FlushAsync(ct);
                Close(4);
            }
            byte[] permit = new byte[33];
            var input = Console.OpenStandardInput();
            int read = await input.ReadAtLeastAsync(permit, permit.Length, throwOnEndOfStream: false, ct);
            return read == permit.Length && permit.AsSpan().SequenceEqual(ready);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return false; }
    }

    internal static async Task<byte[]> ReadRecordAsync(string path, CancellationToken ct, Func<Task>? inspected = null, int maximumBytes = 16384)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous);
        if (file.Length <= 0 || file.Length > maximumBytes)
            throw new InvalidDataException("Media child custody is oversized or empty.");
        byte[] contents = new byte[checked((int)file.Length)];
        if (inspected is not null)
            await inspected();
        await file.ReadExactlyAsync(contents, ct);
        byte[] extra = new byte[1];
        if (await file.ReadAsync(extra, ct) != 0)
            throw new InvalidDataException("Media child custody changed or exceeded its bound.");
        return contents;
    }

    private sealed record AdmissionIdentity(string Platform, ulong Device, ulong Inode);
    private static AdmissionIdentity? ReadAdmission(int descriptor)
    {
        byte[] stat = new byte[512];
        if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FstatDarwin64(descriptor, stat) : Fstat(descriptor, stat)) != 0)
            return null;
        ulong inode = BitConverter.ToUInt64(stat, 8);
        return inode == 0 ? null : new(OperatingSystem.IsMacOS() ? "darwin" : "linux",
            OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0), inode);
    }

    private static (string Platform, string Boot, string Birth, string PidNamespace)? ReadBirth()
    {
        if (OperatingSystem.IsLinux())
        {
            string stat = File.ReadAllText("/proc/self/stat");
            int end = stat.LastIndexOf(')');
            if (end < 0)
                return null;
            string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return null;
            string? pidNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget;
            if (pidNamespace is not { Length: > 6 and <= 128 } || !pidNamespace.StartsWith("pid:[", StringComparison.Ordinal) || !pidNamespace.EndsWith(']') ||
                !ulong.TryParse(pidNamespace.AsSpan(5, pidNamespace.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) || number == 0)
                return null;
            return ("linux", File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), fields[19], pidNamespace);
        }
        if (OperatingSystem.IsMacOS())
        {
            byte[] info = new byte[136];
            byte[] boot = new byte[16];
            nuint length = 16;
            if (ProcInfo(Environment.ProcessId, 3, 0, info, info.Length) != info.Length ||
                Sysctl("kern.boottime", boot, ref length, 0, 0) != 0 || length != 16)
                return null;
            return ("darwin", Pair(BitConverter.ToUInt64(boot, 0), BitConverter.ToUInt64(boot, 8)),
                Pair(BitConverter.ToUInt64(info, 120), BitConverter.ToUInt64(info, 128)), "host");
        }
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return ("windows", "job-object", current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), "host");
    }
    private static string Pair(ulong first, ulong second) => FormattableString.Invariant($"{first}:{second}");
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (!seen.Add(field.Name) || !Unique(field.Value))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray())
                if (!Unique(element))
                    return false;
        return true;
    }
    [DllImport("libc", EntryPoint = "pread", SetLastError = true)] private static extern nint Pread(int descriptor, [Out] byte[] bytes, nuint count, long offset);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
    [DllImport("libproc", EntryPoint = "proc_pidinfo", SetLastError = true)] private static extern int ProcInfo(int pid, int flavor, ulong argument, [Out] byte[] buffer, int size);
    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)] private static extern int Sysctl([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] value, ref nuint length, nint replacement, nuint replacementLength);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(int descriptor, [Out] byte[] stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int FstatDarwin64(int descriptor, [Out] byte[] stat);
}

internal static class NativeJob
{
    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenJob(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
    public static bool Join(string name)
    {
        nint handle = OpenJob(1 | 4, false, name);
        if (handle == 0)
            return false;
        try
        { return AssignProcessToJobObject(handle, GetCurrentProcess()); }
        finally { CloseHandle(handle); }
    }
}

internal static class NativeSession
{
    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSession();
    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static extern int GetLimit(int resource, out Limit limit);
    [StructLayout(LayoutKind.Sequential)]
    private struct Limit { public ulong Current; public ulong Maximum; }

    public static int Create() => SetSession();
    public static bool TryLimits(out MediaWorkerLimits? limits)
    {
        limits = null;
        if (GetLimit(9, out var address) != 0 || GetLimit(2, out var data) != 0 || GetLimit(0, out var cpu) != 0 || GetLimit(4, out var core) != 0 ||
            address.Current > long.MaxValue || data.Current > long.MaxValue || cpu.Current > long.MaxValue || core.Current > long.MaxValue)
            return false;
        limits = new((long)address.Current, (long)data.Current, (long)cpu.Current, (long)core.Current);
        return true;
    }
}


internal static class NativePriority
{
    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, uint who, int priority);
    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int GetPriority(int which, uint who);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(nint process, uint priority);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(nint process);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    public static bool TryReduce(out string kind, out int priority)
    {
        if (OperatingSystem.IsWindows())
        {
            kind = "windows-priority";
            var process = GetCurrentProcess();
            if (!SetPriorityClass(process, 0x4000))
            { priority = 0; return false; }
            priority = (int)GetPriorityClass(process);
            return priority == 0x4000;
        }
        kind = "unix-nice";
        if (!TryReadPriority(out priority))
            return false;
        if (priority is >= 10 and <= 19)
            return true;
        // Linux CLR threads must inherit the launcher priority. Lowering only
        // this managed thread after startup cannot establish that policy.
        if (OperatingSystem.IsLinux())
            return false;
        if (SetPriority(0, 0, 10) != 0)
        { priority = 0; return false; }
        return TryReadPriority(out priority) && priority is >= 10 and <= 19;
    }
    private static bool TryReadPriority(out int priority)
    {
        Marshal.SetLastPInvokeError(0);
        priority = GetPriority(0, 0);
        // -1 is a valid nice value; distinguish it from a native read failure.
        return priority != -1 || Marshal.GetLastPInvokeError() == 0;
    }
}
