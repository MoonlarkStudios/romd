using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Toolchains.Results;
using Moonlark.Libchdr.Benchmarks;
using Xunit;
using BenchmarkProgram = Moonlark.Libchdr.Benchmarks.Program;

namespace Moonlark.Native.Engineering.Tests.Benchmarks;

/// <summary>The report's allocation evidence: raw GC windows per launch, projected only when all three are valid.</summary>
public sealed class LaunchAllocationTests
{
    /// <summary>Unavailable allocation counters remain explicit null evidence in the serialized report.</summary>
    [Theory]
    [InlineData(null, 100L)]
    [InlineData(0L, 0L)]
    [InlineData(1L, -1L)]
    public void MissingAllocationIsPersistedAsNull(long? allocated, long operations)
    {
        CaseMeasurement row = Row(BenchmarkProgram.AllocationPerOperation(allocated, operations));
        string json = JsonSerializer.Serialize(new[] { row }, BenchmarkMatrix.JsonOptions);
        Assert.Null(JsonNode.Parse(json)![0]!["AllocatedBytesPerOperation"]);
    }

    /// <summary>A positive allocation window survives serialization regardless of which launch reports it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryLaunchAllocationSurvivesSerialization(int positive)
    {
        ExecuteResult[] launches = Enumerable.Range(0, 3).Select(index => Launch(index + 1, index == positive ? 32 : 0)).ToArray();
        double? allocation = BenchmarkProgram.AllocationForLaunches(launches, out BenchmarkProgram.LaunchGcEvidence[] evidence);
        Assert.Equal(3, evidence.Length);
        Assert.Equal(32, evidence[positive].AllocatedBytes);
        Assert.Equal(8388608, evidence[positive].Operations);
        Assert.Equal([positive + 1], evidence[positive].LaunchIndices);
        Assert.Equal("// GC:  0 0 0 32 8388608", Assert.Single(evidence[positive].RawGcLines));
        Assert.Equal(32.0 / 8388608, allocation);
        string json = JsonSerializer.Serialize(new[] { Row(allocation) }, BenchmarkMatrix.JsonOptions);
        Assert.Equal(32.0 / 8388608, (double?)JsonNode.Parse(json)![0]!["AllocatedBytesPerOperation"]);
    }

    /// <summary>All three exact-zero GC windows report zero allocation.</summary>
    [Fact]
    public void ThreeZeroAllocationWindowsReportZero() => Assert.Equal(0, BenchmarkProgram.AllocationForLaunches([Launch(1), Launch(2), Launch(3)], out _));

    /// <summary>Missing, duplicated or invalid launch windows cannot be replaced by the final launch's valid zero.</summary>
    [Theory]
    [InlineData("missing-launch")]
    [InlineData("extra-launch")]
    [InlineData("duplicate-launch")]
    [InlineData("unknown-launch")]
    [InlineData("missing-result")]
    [InlineData("failed-process")]
    [InlineData("missing-gc")]
    [InlineData("duplicate-gc")]
    [InlineData("missing-counter")]
    [InlineData("negative-counter")]
    [InlineData("negative-gc")]
    [InlineData("zero-operations")]
    [InlineData("extra-counter")]
    public void InvalidLaunchWindowReportsNoAllocation(string change)
    {
        List<ExecuteResult> launches = [Launch(1), Launch(2), Launch(3)];
        switch (change)
        {
            case "missing-launch":
                launches.RemoveAt(0);
                break;
            case "extra-launch":
                launches.Insert(0, Launch(4));
                break;
            case "duplicate-launch":
                launches[0] = Launch(2);
                break;
            case "unknown-launch":
                launches[0] = Launch(0);
                break;
            case "missing-result":
                launches[0] = Launch(1, results: []);
                break;
            case "failed-process":
                launches[0] = Launch(1, exitCode: 1);
                break;
            case "missing-gc":
                launches[0] = Launch(1, gc: []);
                break;
            case "duplicate-gc":
                launches[0] = Launch(1, gc: ["// GC:  0 0 0 0 8388608", "// GC:  0 0 0 0 8388608"]);
                break;
            case "missing-counter":
                launches[0] = Launch(1, gc: ["// GC:  0 0 0 ? 8388608"]);
                break;
            case "negative-counter":
                launches[0] = Launch(1, gc: ["// GC:  0 0 0 -1 8388608"]);
                break;
            case "negative-gc":
                launches[0] = Launch(1, gc: ["// GC:  -1 0 0 0 8388608"]);
                break;
            case "zero-operations":
                launches[0] = Launch(1, gc: ["// GC:  0 0 0 0 0"]);
                break;
            case "extra-counter":
                launches[0] = Launch(1, gc: ["// GC:  0 0 0 0 8388608 extra"]);
                break;
            default:
                throw new InvalidOperationException(change);
        }
        Assert.Null(BenchmarkProgram.AllocationForLaunches(launches, out _));
    }

    /// <summary>The pinned BDN execution parser itself rejects overflow and nonfinite GC counters before projection.</summary>
    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("NaN")]
    public void InvalidGcCannotBecomeExecutionResult(string counter) =>
        Assert.Throws<NotSupportedException>(() => Launch(1, gc: ["// GC:  0 0 0 " + counter + " 8388608"]));

    private static ExecuteResult Launch(int index, long allocated = 0, string[]? gc = null, string[]? results = null, int exitCode = 0)
    {
        gc ??= ["// GC:  0 0 0 " + allocated + " 8388608"];
        results ??= ["WorkloadResult   1: 8388608 op, 8388608.00 ns, 1.0000 ns/op"];
        return new ExecuteResult(true, exitCode, 1000 + index, results, gc, [.. results, .. gc], index);
    }

    private static CaseMeasurement Row(double? allocation) => new("dvd-zstd", "Hunk", "Safe", 1200, 10, allocation, 3);
}
