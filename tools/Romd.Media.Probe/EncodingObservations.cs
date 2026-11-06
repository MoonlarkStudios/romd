using System.Globalization;

namespace Romd.Media.Probe;

/// <summary>Samples only this probe's exact encoding child; transient /proc races are counted.</summary>
internal sealed record EncodingObservations(int ProcessId, int Samples, long PeakResidentBytes,
    int MinimumNice, int MaximumNice, IReadOnlyList<EncodingThreadObservation> Threads, int ReadRaces)
{
    public static async Task<EncodingObservations> SampleAsync(string sourcePath, Task operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int pid = 0, samples = 0, races = 0;
        long peak = 0;
        var threads = new Dictionary<int, EncodingThreadObservation>();
        while (!operation.IsCompleted)
        {
            foreach (string directory in pid == 0 ? Directory.EnumerateDirectories("/proc") : ["/proc/" + pid])
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int candidate))
                    continue;
                try
                {
                    string[] leader = Stat(await File.ReadAllTextAsync(Path.Combine(directory, "stat"), CancellationToken.None));
                    if (int.Parse(leader[1], CultureInfo.InvariantCulture) != Environment.ProcessId)
                        continue;
                    string[] arguments = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory, "cmdline"), CancellationToken.None))
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (!IsEncodingRuntime(arguments, sourcePath))
                        continue;
                    Check.That(pid == 0 || pid == candidate, "one encoding child identity across samples");
                    pid = candidate;
                    foreach (string line in await File.ReadAllLinesAsync(Path.Combine(directory, "status"), CancellationToken.None))
                    {
                        if (!line.StartsWith("VmHWM:", StringComparison.Ordinal))
                            continue;
                        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        peak = Math.Max(peak, checked(long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024));
                    }
                    foreach (string task in Directory.EnumerateDirectories(Path.Combine(directory, "task")))
                    {
                        try
                        {
                            int tid = int.Parse(Path.GetFileName(task), CultureInfo.InvariantCulture);
                            var current = ReadThreadStat(tid, await File.ReadAllTextAsync(Path.Combine(task, "stat"), CancellationToken.None));
                            threads[tid] = threads.TryGetValue(tid, out var prior)
                                ? prior with
                                {
                                    Samples = prior.Samples + 1,
                                    MinimumNice = Math.Min(prior.MinimumNice, current.MinimumNice),
                                    MaximumNice = Math.Max(prior.MaximumNice, current.MaximumNice),
                                    CpuTicks = Math.Max(prior.CpuTicks, current.CpuTicks)
                                }
                                : current;
                        }
                        catch (IOException) { races++; }
                    }
                    samples++;
                }
                catch (IOException) { races++; }
            }
            await Task.WhenAny(operation, Task.Delay(100, CancellationToken.None));
        }
        return new(pid, samples, peak, threads.Count == 0 ? 0 : threads.Values.Min(t => t.MinimumNice),
            threads.Count == 0 ? 0 : threads.Values.Max(t => t.MaximumNice), threads.Values.OrderBy(t => t.ThreadId).ToArray(), races);
    }

    private static string[] Stat(string text) => text[(text.LastIndexOf(')') + 2)..]
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    internal static EncodingThreadObservation ReadThreadStat(int tid, string text)
    {
        string[] fields = Stat(text);
        int nice = int.Parse(fields[16], CultureInfo.InvariantCulture);
        long ticks = checked(long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture));
        return new(tid, 1, nice, nice, ticks, ticks);
    }

    internal static bool IsEncodingRuntime(string[] arguments, string sourcePath)
    {
        int encoding = Array.IndexOf(arguments, "encode");
        return arguments.Length > 0 && Path.GetFileName(arguments[0]) == "dotnet" &&
            encoding == 2 && encoding + 1 < arguments.Length && arguments[encoding + 1] == sourcePath &&
            Path.GetFileName(arguments[encoding - 1]) == "Romd.Media.Worker.dll";
    }
}

internal sealed record EncodingThreadObservation(int ThreadId, int Samples, int MinimumNice, int MaximumNice, long CpuTicks, long FirstCpuTicks)
{
    public long ObservedCpuTicks => Math.Max(0, CpuTicks - FirstCpuTicks);
}
