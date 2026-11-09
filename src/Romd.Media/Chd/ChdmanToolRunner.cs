using System.Diagnostics;
using System.Globalization;
using Romd.Media.Inspection;
using Romd.Media.Processes;

namespace Romd.Media.Chd;

internal sealed record ChdmanProcessExpectation(int Nice, string Cpu);

internal sealed record ChdmanToolRun(MediaWorkerStatus Status, ChdDvdEncodingRejection? Rejection, ChdDvdToolDiagnostics? Diagnostics);

/// <summary>
///     Runs the verified chdman under prlimit with the reserved output cap applied during writes. Both programs
///     execute from sealed images: chdman's sealed bytes must match its pinned identity before any process
///     starts, so the executed image is known by construction rather than observed after exec.
/// </summary>
/// <remarks>
///     The tool inherits the worker's process group, lowered priority and CPU, memory and core limits, and runs
///     with an empty environment. It also inherits the admission lease at descriptor 3 and the two sealed,
///     read-only images at descriptors 4 and 5.
/// </remarks>
internal static class ChdmanToolRunner
{
    internal const int DiagnosticTailBytes = 4096;
    internal const long MaximumDiagnosticBytes = 256L << 10;

    internal const string WrapperPath = "/usr/bin/prlimit";
    // Child descriptor layout from PosixMediaChild.StartTool: images inherit at 4, 5 in order.
    private const string ToolImage = "/proc/self/fd/4", WrapperImage = "/proc/self/fd/5";

    internal static async Task<ChdmanToolRun> RunAsync(ChdmanCandidate candidate, IReadOnlyList<string> arguments,
        long outputCap, int admissionDescriptor, ChdmanProcessExpectation inherited, CancellationToken ct)
    {
        SealedExecutable tool;
        try
        { tool = SealedExecutable.Seal(candidate.BinaryPath, "chdman", ct); }
        catch (InvalidDataException) { return Rejected(ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch, candidate.BinaryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Rejected(ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LaunchFailure, candidate.BinaryPath); }
        using (tool)
        {
            // The verified path may have changed since Verify; only the sealed bytes decide what executes.
            if (tool.Bytes != candidate.ExpectedIdentity.BinaryBytes || tool.Sha256 != candidate.ExpectedIdentity.BinarySha256)
                return Rejected(ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch, candidate.BinaryPath);
            SealedExecutable wrapper;
            try
            { wrapper = SealedExecutable.Seal(WrapperPath, "prlimit", ct); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { return Rejected(ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LaunchFailure, WrapperPath); }
            using (wrapper)
            {
                var start = new ProcessStartInfo(WrapperImage) { UseShellExecute = false };
                start.Environment.Clear();
                string cap = outputCap.ToString(CultureInfo.InvariantCulture);
                foreach (string argument in (string[])["--fsize=" + cap + ":" + cap, "--", ToolImage, .. arguments])
                    start.ArgumentList.Add(argument);
                return await RunPreparedAsync(start, admissionDescriptor, [tool, wrapper], candidate, inherited, ct);
            }
        }
    }

    private static ChdmanToolRun Rejected(ChdDvdEncodingStage stage, ChdDvdEncodingCause cause, string path) =>
        new(MediaWorkerStatus.IoFailure, new(stage, cause, path), null);

    // Attribute from the owned final CPU clock and joined status, never a signal alone.
    // Adjusted wait4 usage remains a conservative fallback; retain the 20 ms allowance.
    internal static bool HasCpuBudgetEvidence(int signal, string inheritedCpu, long cpuMicroseconds) =>
        signal is 9 or 24 &&
        long.TryParse(inheritedCpu.Split(' ')[0], CultureInfo.InvariantCulture, out long seconds) &&
        seconds > 0 && cpuMicroseconds >= seconds * 1_000_000 - 20_000;

    /// <summary>Runs a launch prepared by <see cref="ChdmanToolRunner"/>; the executed images are already established.</summary>
    internal static async Task<ChdmanToolRun> RunPreparedAsync(ProcessStartInfo start, int descriptor, IReadOnlyList<SealedExecutable> images,
        ChdmanCandidate candidate, ChdmanProcessExpectation inherited, CancellationToken ct)
    {
        using var stopDrains = new CancellationTokenSource();
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long total = 0;
        using PosixMediaChild tool = PosixMediaChild.StartTool(start, descriptor, images);
        // Nothing waits on the thread pool before the pipes are read, so both drains start at once.
        Task<Capture> output = Drain(tool.Output), error = Drain(tool.Error);
        ChdDvdEncodingRejection? failure = null;
        MediaWorkerStatus status = MediaWorkerStatus.Malformed;
        try
        {
            tool.Input.Dispose();
            Task exit = tool.WaitAsync(ct);
            await Task.WhenAny(exit, overflow.Task, drainFailure.Task);
            ct.ThrowIfCancellationRequested();
            if (overflow.Task.IsCompleted)
            { status = MediaWorkerStatus.LimitExceeded; failure = RejectExecution(ChdDvdEncodingCause.DiagnosticLimitExceeded); }
            else if (drainFailure.Task.IsCompleted)
            { status = MediaWorkerStatus.IoFailure; failure = RejectExecution(ChdDvdEncodingCause.DrainFailure); }
            else
            {
                await exit;
                if (tool.ExitCode != 0)
                {
                    bool cpuBudget = HasCpuBudgetEvidence(tool.TerminatingSignal, inherited.Cpu, tool.CpuMicroseconds);
                    status = cpuBudget ? MediaWorkerStatus.LimitExceeded : MediaWorkerStatus.Malformed;
                    failure = RejectExecution(cpuBudget ? ChdDvdEncodingCause.CpuBudgetExceeded : ChdDvdEncodingCause.ToolExitFailure);
                }
            }
        }
        finally
        {
            // Sole helper owns exact kill/reap. No group kill, LOCK_UN, partial-output deletion or tree-stop claim.
            try
            { await tool.TerminateAndWaitAsync(); }
            finally
            {
                // WhenAll waits for every started task before propagating any fault or cancellation.
                // A descendant retaining pipes keeps this worker pending for the outer wall limit/group cleanup.
                // No outcome or caller cancellation escapes while a started drain remains unjoined.
                await Task.WhenAll(output, error);
            }
        }
        Capture stdout = await output, stderr = await error;
        if (overflow.Task.IsCompleted)
        { status = MediaWorkerStatus.LimitExceeded; failure = RejectExecution(ChdDvdEncodingCause.DiagnosticLimitExceeded); }
        return new(status, failure, new(tool.Id, tool.ExitCode, stdout.Bytes, stderr.Bytes, stdout.Tail, stderr.Tail, tool.CpuMicroseconds));
        ChdDvdEncodingRejection RejectExecution(ChdDvdEncodingCause cause) => new(ChdDvdEncodingStage.ToolExecution, cause, candidate.BinaryPath);
        async Task<Capture> Drain(Stream stream)
        {
            byte[] buffer = new byte[4096], tail = new byte[DiagnosticTailBytes];
            int tailCount = 0;
            long bytes = 0;
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, stopDrains.Token);
                    if (read == 0)
                        break;
                    bytes = checked(bytes + read);
                    if (Interlocked.Add(ref total, read) > MaximumDiagnosticBytes)
                        overflow.TrySetResult();
                    int retained = Math.Min(read, tail.Length), keep = Math.Min(tailCount, tail.Length - retained);
                    if (keep > 0)
                        Buffer.BlockCopy(tail, tailCount - keep, tail, 0, keep);
                    Buffer.BlockCopy(buffer, read - retained, tail, keep, retained);
                    tailCount = keep + retained;
                }
                return new(bytes, tail.AsSpan(0, tailCount).ToArray());
            }
            catch { drainFailure.TrySetResult(); throw; }
        }
    }

    private sealed record Capture(long Bytes, byte[] Tail);

    /// <summary>The worker's own lowered priority and CPU limit, which the tool inherits unchanged across spawn and exec.</summary>
    internal static ChdmanProcessExpectation CurrentExpectation()
    {
        string stat = ReadProc("/proc/self/stat");
        int close = stat.LastIndexOf(')');
        if (close < 0)
            throw new InvalidDataException("Malformed process stat.");
        string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = ReadProc("/proc/self/limits").Split('\n').Single(value => value.StartsWith("Max cpu time ", StringComparison.Ordinal));
        string[] cpu = line["Max cpu time".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cpu.Length != 3)
            throw new InvalidDataException("Malformed process limits.");
        return new(int.Parse(fields[16], CultureInfo.InvariantCulture), cpu[0] + " " + cpu[1]);
    }
    private static string ReadProc(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[16385];
        int length = 0;
        while (length < bytes.Length)
        { int read = file.Read(bytes, length, bytes.Length - length); if (read == 0) break; length += read; }
        if (length > 16384)
            throw new InvalidDataException("Process observation exceeds its bound.");
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }
}
