using System.Globalization;
using System.Text.RegularExpressions;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Chd;

/// <summary>The libchdr fuzz harness bounds headers with ROMD's DVD CHD preflight limits, so every header ROMD admits reaches
/// libchdr in the fuzzer, and a harness-only bound rejects only files ROMD refuses anyway (native/libchdr/FUZZING.md).</summary>
public sealed class ChdFuzzHarnessBoundsTests
{
    private static readonly string Harness =
        Path.Combine(NativeTestBuilds.RepositoryRoot, "native", "libchdr", "fuzz", "chd_walk_fuzzer.c");

    [Fact]
    public void HarnessBounds_ChdWalkFuzzer_EqualDvdPreflightLimits()
    {
        string source = File.ReadAllText(Harness);
        Match hunkBytes = Regex.Match(source, @"^#define MAX_HUNK_BYTES \(1u << ([0-9]+)\)", RegexOptions.Multiline);
        Match hunkCount = Regex.Match(source, @"^#define MAX_HUNK_COUNT ([0-9]+)u\b", RegexOptions.Multiline);

        hunkBytes.Success.ShouldBeTrue("chd_walk_fuzzer.c must define MAX_HUNK_BYTES as (1u << n); update this test with its form");
        hunkCount.Success.ShouldBeTrue("chd_walk_fuzzer.c must define MAX_HUNK_COUNT as a decimal literal with a u suffix; update this test with its form");
        int shift = int.Parse(hunkBytes.Groups[1].Value, CultureInfo.InvariantCulture);
        shift.ShouldBeLessThan(32);
        (1U << shift).ShouldBe(ChdDvdLimits.MaximumHunkBytes);
        uint.Parse(hunkCount.Groups[1].Value, CultureInfo.InvariantCulture).ShouldBe(ChdDvdLimits.MaximumHunkCount);
    }
}
