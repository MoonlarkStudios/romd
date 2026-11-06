using System.Diagnostics;
using System.Runtime.InteropServices;
using Moonlark.Libchdr.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies that every malformed input fails with a typed result, with no crash, hang or overrun, and with no
/// output that depends on prior memory, read-ahead or read order.</summary>
/// <remarks>
/// The inputs run in separate CorpusRunner processes, never in the test host, under a managed heap limit and a
/// wall-clock kill, which bounds a hang while the test host lives. On Unix a CPU-time limit also bounds a CPU-bound
/// runner orphaned by a dying test host; Windows has no CPU limit. No OS limit bounds native memory, so each runner
/// reports its peak working set, which must stay under a ceiling where the platform reports it. A batch that fails is
/// rerun one file at a time, so each failure is named. A batch crash no single file reproduces fails too, since the
/// inputs then interact; a batch that only overran its time while every input passes alone fails as over its budget. An
/// overall deadline, checked before every process, keeps a broken build from running for hours. Only failing inputs
/// are kept, including every input of a batch whose inputs interact, and the failure message names their directory.
/// </remarks>
public sealed class MalformedCorpusTests(ITestOutputHelper output)
{
    private const int BatchSize = 40;
    private const long PeakWorkingSetCeiling = 2L << 30;
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SingleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    /// <summary>Every corpus input is rejected or read with typed errors only, and passes every runner check, in processes
    /// that exit normally in time.</summary>
    [Fact]
    public void EveryMalformedInputFailsTypedWithoutCrashingOrHanging()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("moonlark-corpus-");
        var files = new List<string>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach ((string name, byte[] bytes) in MalformedCorpus.Build())
            {
                string path = Path.Combine(directory.FullName, name + ".chd");
                File.WriteAllBytes(path, bytes);
                files.Add(path);
            }
            var results = new Dictionary<string, string>(StringComparer.Ordinal);
            var failures = new List<string>();
            Stopwatch clock = Stopwatch.StartNew();
            long peak = RunAll(files, results, failures, kept, clock);
            foreach ((string file, string result) in results.Where(result => !Passed(result.Value)))
            {
                failures.Add($"{file}: {result}");
                kept.Add(file);
            }
            foreach (IGrouping<string, string> kind in results.Values.GroupBy(value => string.Join(' ', value.Split(' ').Take(2))))
                output.WriteLine($"{kind.Count(),5}  {kind.Key}");
            output.WriteLine($"{files.Count} inputs in {clock.Elapsed.TotalSeconds:0.0} s; highest runner peak memory {peak >> 20} MiB");
            string where = kept.Count > 0 ? $", the failing inputs kept in {directory.FullName}" : "";
            Assert.True(failures.Count == 0, $"{failures.Count} failures among {files.Count} inputs{where}:\n" + string.Join('\n', failures));
            Assert.Equal(files.Count, results.Count);
            Assert.Contains(results.Values, value => value.StartsWith("rejected", StringComparison.Ordinal));
            Assert.Contains(results.Values, value => value.StartsWith("read", StringComparison.Ordinal));
        }
        finally
        {
            if (kept.Count == 0)
                directory.Delete(recursive: true);
            else
                foreach (string path in files.Where(path => !kept.Contains(Path.GetFileName(path))))
                    File.Delete(path);
        }
    }

    /// <summary>Runs the inputs in batches, rerunning a failing batch one file at a time, until the deadline; returns the
    /// highest peak working set a runner reported.</summary>
    private static long RunAll(List<string> files, Dictionary<string, string> results, List<string> failures, HashSet<string> kept, Stopwatch clock)
    {
        long peak = 0;
        bool Expired()
        {
            if (clock.Elapsed <= Deadline)
                return false;
            failures.Add($"deadline: stopped after {clock.Elapsed.TotalSeconds:0} s with {results.Count} of {files.Count} inputs done");
            return true;
        }
        foreach (string[] batch in files.Chunk(BatchSize))
        {
            if (Expired())
                break;
            Outcome outcome = Run(batch, BatchTimeout);
            if (outcome.Clean(batch))
            { peak = Math.Max(peak, Record(outcome, Name(batch), results, failures, kept)); continue; }
            bool reproduced = false;
            foreach (string path in batch)
            {
                if (Expired())
                    return peak;
                Outcome single = Run([path], SingleTimeout);
                string file = Path.GetFileName(path);
                if (single.Clean([path]))
                    peak = Math.Max(peak, Record(single, file, results, failures, kept));
                else
                { failures.Add($"{file}: {single.Describe(file)}"); kept.Add(file); reproduced = true; }
            }
            if (reproduced)
                continue;
            if (!outcome.Exited)
            {
                failures.Add($"{Name(batch)}: over its {BatchTimeout.TotalSeconds:0} s budget, but every input passes alone; the corpus is too slow here, not failing");
                continue;
            }
            failures.Add($"{Name(batch)}: {outcome.Describe(null)}, but no single input reproduces it, so the inputs interact");
            kept.UnionWith(batch.Select(path => Path.GetFileName(path)));
        }
        return peak;
    }

    private static bool Passed(string result) =>
        result.StartsWith("rejected ", StringComparison.Ordinal) || result.StartsWith("read ", StringComparison.Ordinal);

    private static string Name(string[] batch) => $"batch {Path.GetFileName(batch[0])} .. {Path.GetFileName(batch[^1])}";

    /// <summary>Keeps the results of a clean run and checks its peak working set, which it returns.</summary>
    private static long Record(Outcome outcome, string name, Dictionary<string, string> results, List<string> failures, HashSet<string> kept)
    {
        foreach ((string file, string result) in outcome.Results)
            results[file] = result;
        if (outcome.PeakWorkingSet > PeakWorkingSetCeiling)
        {
            failures.Add($"{name}: peak working set {outcome.PeakWorkingSet >> 20} MiB exceeds {PeakWorkingSetCeiling >> 20} MiB");
            kept.UnionWith(outcome.Results.Keys);
        }
        return outcome.PeakWorkingSet;
    }

    private static Outcome Run(string[] files, TimeSpan timeout)
    {
        (string rid, string filename) = NativePlatform.Current();
        string library = Path.Combine(NativeTestEnvironment.Root, "artifacts", "native", "libchdr", rid, "native", filename);
        string runner = Path.Combine(AppContext.BaseDirectory, "Moonlark.Libchdr.CorpusRunner.dll");
        var start = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (OperatingSystem.IsWindows())
            start.FileName = Host();
        else
        {
            start.FileName = "/bin/sh";
            foreach (string argument in (string[])["-c", "ulimit -t 60 && exec \"$0\" \"$@\"", Host()])
                start.ArgumentList.Add(argument);
        }
        foreach (string argument in (string[])[runner, library, .. files])
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x40000000";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeout);
        if (!exited)
            process.Kill(entireProcessTree: true);
        process.WaitForExit();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        long peak = 0;
        foreach (string line in stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length != 2)
                continue;
            if (parts[0] == "#peak-working-set")
                _ = long.TryParse(parts[1], out peak);
            else
                results[parts[0]] = parts[1];
        }
        return new Outcome(exited, process.ExitCode, results, peak, stderr.Result);
    }

    /// <summary>The dotnet host: DOTNET_HOST_PATH under dotnet test, otherwise the host that owns this runtime.</summary>
    private static string Host()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host)
            return host;
        string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? candidate : throw new FileNotFoundException("No dotnet host was found to run the corpus.", candidate);
    }

    private sealed record Outcome(bool Exited, int ExitCode, Dictionary<string, string> Results, long PeakWorkingSet, string Errors)
    {
        /// <summary>Exit 0, or 3 when a failing check was reported as a result, with one result per input.</summary>
        internal bool Clean(string[] files) =>
            Exited && ExitCode is 0 or 3 && files.All(path => Results.ContainsKey(Path.GetFileName(path)));

        internal string Describe(string? file) => !Exited
            ? "hung (killed at the wall-clock limit)"
            : file is not null && Results.TryGetValue(file, out string? result)
                ? $"exit {ExitCode}{Signal(ExitCode)} after {result}"
                : $"crashed with exit {ExitCode}{Signal(ExitCode)}: {Errors.Split('\n').FirstOrDefault()}";

        private static string Signal(int exitCode) => exitCode switch
        {
            134 => " (SIGABRT, such as a stack overflow)",
            137 => " (SIGKILL)",
            138 => " (SIGBUS)",
            139 => " (SIGSEGV)",
            152 => " (SIGXCPU, the CPU-time limit)",
            _ => "",
        };
    }
}
