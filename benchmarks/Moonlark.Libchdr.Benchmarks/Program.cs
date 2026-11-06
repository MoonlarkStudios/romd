using System.Text.Json;
using System.Globalization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Toolchains.Results;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.Common;
using Perfolizer.Mathematics.OutlierDetection;

namespace Moonlark.Libchdr.Benchmarks;

internal static class Program
{
    /// <summary>Runs the matrix and records a report; only execution or provenance failures fail, never a measurement.</summary>
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != "run")
        {
            Console.Error.WriteLine("Usage: run <new-artifacts-directory>");
            return 1;
        }
        string root = FindRoot();
        string output = Path.GetFullPath(arguments[1]);
        string artifacts = Path.GetFullPath(Path.Combine(root, "artifacts")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifacts, StringComparison.Ordinal) || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Benchmark output must be a new directory below this checkout's artifacts.");
        Directory.CreateDirectory(output);
        string inputs = Path.Combine(output, "inputs.json");
        string runId = RunInputs.Write(root, inputs);
        string inputsHash = RunInputs.Hash(inputs);
        var toolchain = new ChildBuildToolchain(root, inputs, inputsHash);
        Environment.SetEnvironmentVariable("MOONLARK_BENCHMARK_ROOT", root);
        Environment.SetEnvironmentVariable("MOONLARK_BENCHMARK_INPUTS", inputs);
        Environment.SetEnvironmentVariable("MOONLARK_BENCHMARK_INPUTS_SHA256", inputsHash);
        ManualConfig config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default.WithId("ThreeLaunches-" + runId).WithToolchain(toolchain).WithLaunchCount(3).WithWarmupCount(6)
                .WithMinIterationCount(15).WithMaxIterationCount(50).WithOutlierMode(OutlierMode.DontRemove))
            .AddExporter(JsonExporter.Full)
            .WithArtifactsPath(output)
            .WithOption(ConfigOptions.KeepBenchmarkFiles, true);
        CaseMeasurement[] measurements = [];
        List<CaseAllocationEvidence> allocations = [];
        string? failure = null;
        try
        {
            Summary summary = BenchmarkRunner.Run<ReadBenchmarks>(config);
            measurements = summary.Reports.Where(report => report.Success && report.ResultStatistics is not null)
                .Select(report => Measurement(report, allocations)).ToArray();
        }
        catch (Exception exception) { failure = "Benchmark execution failed: " + exception.Message; }
        try
        { toolchain.VerifyCompleted(); }
        catch (Exception exception) { failure = (failure is null ? "" : failure + " ") + "Build provenance failed: " + exception.Message; }
        File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(measurements, BenchmarkMatrix.JsonOptions));
        File.WriteAllText(Path.Combine(output, "launch-allocations.json"), JsonSerializer.Serialize(allocations, BenchmarkMatrix.JsonOptions));
        string report = Path.Combine(output, "report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            Failure = failure,
            InputsSha256 = inputsHash,
            ChildBuilds = toolchain.Bindings,
            MeasurementsSha256 = RunInputs.Hash(Path.Combine(output, "measurements.json")),
            LaunchAllocationsSha256 = RunInputs.Hash(Path.Combine(output, "launch-allocations.json")),
        }, BenchmarkMatrix.JsonOptions));
        Console.WriteLine($"{measurements.Length} measured cases; report: {report}");
        if (failure is null)
            return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static CaseMeasurement Measurement(BenchmarkReport report, List<CaseAllocationEvidence> allocations)
    {
        var statistics = report.ResultStatistics!;
        ConfidenceInterval interval = statistics.GetConfidenceInterval(ConfidenceLevel.L95);
        double? allocationPerOperation = AllocationForLaunches(report.ExecuteResults, out LaunchGcEvidence[] launches);
        var measurement = new CaseMeasurement(
            (string)report.BenchmarkCase.Parameters.Items.Single(item => item.Name == nameof(ReadBenchmarks.Fixture)).Value,
            (string)report.BenchmarkCase.Parameters.Items.Single(item => item.Name == nameof(ReadBenchmarks.Operation)).Value,
            report.BenchmarkCase.Descriptor.WorkloadMethod.Name, statistics.Mean,
            (interval.Upper - interval.Lower) / 2, allocationPerOperation,
            report.GetResultRuns().Select(run => run.LaunchIndex).Distinct().Count());
        allocations.Add(new(measurement.Fixture, measurement.Operation, measurement.Method, launches));
        return measurement;
    }

    private sealed record CaseAllocationEvidence(string Fixture, string Operation, string Method, LaunchGcEvidence[] Launches);
    internal sealed record LaunchGcEvidence(int[] LaunchIndices, bool Successful, string[] RawGcLines, long? AllocatedBytes, long? Operations);

    internal static double? AllocationForLaunches(IReadOnlyList<ExecuteResult> executions, out LaunchGcEvidence[] evidence)
    {
        var records = new List<LaunchGcEvidence>();
        var indices = new HashSet<int>();
        bool valid = executions.Count == 3;
        double maximum = 0;
        foreach (ExecuteResult execution in executions)
        {
            int[] launch = execution.Measurements.Where(item => item.IterationMode == IterationMode.Workload && item.IterationStage == IterationStage.Result)
                .Select(item => item.LaunchIndex).Distinct().ToArray();
            bool successful = execution.IsSuccess && execution.ExitCode == 0;
            string[] lines = execution.PrefixedLines.Where(line => line.StartsWith("// GC:", StringComparison.Ordinal)).ToArray();
            long? allocated = null, operations = null;
            if (lines.Length == 1)
            {
                string[] fields = lines[0][6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // The pinned BDN parser permits extra tokens and signed values; reject them before parsing the raw counters.
                if (fields.Length == 5 && fields.All(field => long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    try
                    {
                        GcStats stats = GcStats.Parse(lines[0]);
                        allocated = stats.GetTotalAllocatedBytes(excludeAllocationQuantumSideEffects: false);
                        operations = stats.TotalOperations;
                    }
                    catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or NotSupportedException) { }
                }
            }
            records.Add(new(launch, successful, lines, allocated, operations));
            if (!successful || launch.Length != 1 || launch[0] is < 1 or > 3 || !indices.Add(launch[0])
                || AllocationPerOperation(allocated, operations ?? 0) is not double ratio)
                valid = false;
            else
                maximum = Math.Max(maximum, ratio);
        }
        evidence = records.ToArray();
        return valid && indices.SetEquals([1, 2, 3]) ? maximum : null;
    }

    internal static double? AllocationPerOperation(long? allocated, long operations) =>
        allocated is >= 0 && operations > 0 ? (double)allocated.Value / operations : null;

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "native", "libchdr", "libchdr.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Benchmarks require the source checkout and its verified synthetic assets.");
    }
}
