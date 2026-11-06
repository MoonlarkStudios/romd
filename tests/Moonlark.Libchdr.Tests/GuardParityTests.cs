using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies the guard-parity check between the libchdr fuzz harness's mirrored pre-read checks and
/// Moonlark.Libchdr's open path, which <c>scripts/native/fuzz.sh parity</c> runs.</summary>
public sealed class GuardParityTests
{
    private const uint OverBoundHunkBytes = 1 << 24;

    private static readonly string[] Log =
    [
        "INFO: Seed: 1",
        "/out/build/chd_walk_fuzzer: Running 3 inputs 1 time(s) each.",
        "Running: fixtures/dvd-zstd.chd",
        "chd_walk: accept size=900 version=5 codecs=zstd,0,0,0 hunkbytes=16384 hunks=64 metadata=1 budget=0 order=forward read=64 decoded=63",
        "Executed fixtures/dvd-zstd.chd in 3 ms",
        "Running: malformed/bound.chd",
        "chd_walk: reject stage=bound size=200 hunkbytes=16777216 hunks=1 reason=harness bound: hunk bytes",
        "Executed malformed/bound.chd in 0 ms",
        "Running: malformed/truncated.chd",
        "chd_walk: reject stage=envelope size=7 reason=truncated header",
        "Executed malformed/truncated.chd in 0 ms",
        "***",
    ];

    /// <summary>Loads the digest-verified native test asset.</summary>
    public GuardParityTests() => _ = NativeTestEnvironment.Root;

    /// <summary>Each disposition line belongs to the input libFuzzer announced before it, with its stage and fields.</summary>
    [Fact]
    public void HarnessLogPairsEachDispositionWithTheInputLibFuzzerRan()
    {
        Dictionary<string, HarnessDisposition> parsed = GuardParity.ParseHarnessLog(Log);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(new HarnessDisposition(HarnessStage.Accept, 900, 16384, 64, 64, 63, ""), parsed["fixtures/dvd-zstd.chd"]);
        Assert.Equal(new HarnessDisposition(HarnessStage.Bound, 200, OverBoundHunkBytes, 1, 0, 0, "harness bound: hunk bytes"),
            parsed["malformed/bound.chd"]);
        Assert.Equal(new HarnessDisposition(HarnessStage.Envelope, 7, 0, 0, 0, 0, "truncated header"), parsed["malformed/truncated.chd"]);
    }

    /// <summary>A disposition with no announced input, a repeated input, an unknown stage, or a missing or repeated field fails
    /// the parse rather than pairing a disposition with the wrong seed or value.</summary>
    [Theory]
    [InlineData("chd_walk: reject stage=envelope size=7 reason=truncated header")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=open size=7 reason=x\nchd_walk: reject stage=open size=7 reason=y")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=open size=7 reason=x\nRunning: a.chd\nchd_walk: reject stage=open size=7 reason=x")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=sideways size=7 reason=x")]
    [InlineData("Running: a.chd\nchd_walk: reject size=7 reason=x")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=bound size=7 hunks=1 reason=harness bound: hunk bytes")]
    [InlineData("Running: a.chd\nchd_walk: accept size=7 version=5 hunkbytes=16384 hunks=1 read=1")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=envelope size=-7 reason=x")]
    [InlineData("Running: a.chd\nchd_walk: reject stage=open size=7 size=8 reason=x")]
    [InlineData("Running: a.chd\nchd_walk: accept size=7 version=5 hunkbytes=16384 hunks=1 read=1 decoded=1 decoded=0")]
    public void MalformedHarnessLogsFailClosed(string log) =>
        Assert.Throws<InvalidDataException>(() => GuardParity.ParseHarnessLog(log.Split('\n')));

    /// <summary>A rejection before chd_open agrees only with one before it, an admission only with an admission, and a later
    /// rejection or a harness-only bound with a rejection after Moonlark.Libchdr's envelope check; -1 marks a mismatch. The
    /// harness geometry here is beyond the 1 MiB hunk bound, so a bound rejection may agree.</summary>
    [Theory]
    [InlineData("Envelope", "RejectedAtEnvelope", 0)]
    [InlineData("Envelope", "RejectedAfterEnvelope", -1)]
    [InlineData("Envelope", "Admitted", -1)]
    [InlineData("Open", "RejectedAfterEnvelope", 1)]
    [InlineData("Open", "RejectedAtEnvelope", -1)]
    [InlineData("Open", "Admitted", -1)]
    [InlineData("Validation", "RejectedAfterEnvelope", 2)]
    [InlineData("Validation", "RejectedAtEnvelope", -1)]
    [InlineData("Validation", "Admitted", -1)]
    [InlineData("Accept", "Admitted", 3)]
    [InlineData("Accept", "RejectedAtEnvelope", -1)]
    [InlineData("Accept", "RejectedAfterEnvelope", -1)]
    [InlineData("Bound", "RejectedAfterEnvelope", 4)]
    [InlineData("Bound", "Admitted", 5)]
    [InlineData("Bound", "RejectedAtEnvelope", -1)]
    public void EachHarnessStageAgreesOnlyWithItsManagedStage(string harnessStage, string managedStage, int bucket)
    {
        var harness = new HarnessDisposition(Enum.Parse<HarnessStage>(harnessStage), 4096, OverBoundHunkBytes, 1, 0, 0, "reason");
        var managed = new ManagedDisposition(Enum.Parse<ManagedStage>(managedStage), ChdError.None, Header(ChdCodec.Zlib, OverBoundHunkBytes));
        string? mismatch = GuardParity.Compare(harness, managed, 4096);
        if (bucket < 0)
            Assert.NotNull(mismatch);
        else
        {
            Assert.Null(mismatch);
            Assert.Equal(bucket, GuardParity.Agreement(harness, managed));
        }
    }

    /// <summary>A bound rejection agrees with an admission only when libchdr reports the very geometry the harness bounded.</summary>
    [Fact]
    public void ABoundRejectionMustMatchTheGeometryLibchdrReports()
    {
        var harness = new HarnessDisposition(HarnessStage.Bound, 4096, OverBoundHunkBytes, 1, 0, 0, "harness bound: hunk bytes");
        var managed = new ManagedDisposition(ManagedStage.Admitted, ChdError.None, Header(ChdCodec.Zlib, 16384));
        Assert.Contains("hunkbytes=16384", GuardParity.Compare(harness, managed, 4096), StringComparison.Ordinal);
    }

    /// <summary>A bound rejection agrees only with a geometry beyond a harness bound, ROMD's ChdDvdLimits: more than 1 MiB per
    /// hunk or more than 2,621,440 hunks. Within both, it is a mismatch whatever Moonlark.Libchdr does.</summary>
    [Theory]
    [InlineData(1048577UL, 1UL, "Admitted", true)]
    [InlineData(16384UL, 2621441UL, "Admitted", true)]
    [InlineData(1048576UL, 2621440UL, "Admitted", false)]
    [InlineData(16384UL, 64UL, "Admitted", false)]
    [InlineData(1048577UL, 1UL, "RejectedAfterEnvelope", true)]
    [InlineData(16384UL, 2621441UL, "RejectedAfterEnvelope", true)]
    [InlineData(1048576UL, 2621440UL, "RejectedAfterEnvelope", false)]
    public void ABoundRejectionAgreesOnlyBeyondTheHarnessLimits(ulong hunkBytes, ulong hunkCount, string managedStage, bool agrees)
    {
        var harness = new HarnessDisposition(HarnessStage.Bound, 4096, hunkBytes, hunkCount, 0, 0, "harness bound");
        var managed = new ManagedDisposition(Enum.Parse<ManagedStage>(managedStage), ChdError.None,
            Header(ChdCodec.Zlib, (uint)hunkBytes, (uint)hunkCount));
        Assert.Equal(agrees, GuardParity.Compare(harness, managed, 4096) is null);
    }

    /// <summary>A seed the harness never reported, or saw only part of, cannot agree with any managed disposition.</summary>
    [Fact]
    public void AMissingOrTruncatedHarnessReplayIsAMismatch()
    {
        var managed = new ManagedDisposition(ManagedStage.RejectedAtEnvelope, ChdError.InvalidData, default);
        var truncated = new HarnessDisposition(HarnessStage.Envelope, 2097152, 0, 0, 0, 0, "header signature");
        Assert.Equal("the harness reported no disposition", GuardParity.Compare(null, managed, 4096));
        Assert.Equal("the harness saw 2097152 of its 16777340 bytes", GuardParity.Compare(truncated, managed, 16777340));
    }

    /// <summary>The classifier places a rejection before or after chd_open exactly as ChdFile's open path does.</summary>
    [Fact]
    public void ClassifierPlacesEachRejectionBeforeOrAfterChdOpen()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("moonlark-parity-");
        try
        {
            byte[] fixture = File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
            string truncated = Write(directory, "truncated.chd", fixture[..64]);
            string parent = Write(directory, "parent.chd", CraftedChd.V5(CraftedChd.V5Entry.Stored, new(CraftedChd.V5Parent)));
            string mini = Write(directory, "mini.chd", CraftedChd.V4Sized(1, 0, CraftedChd.LegacyEntry.Mini(0x0102030405060708)));
            ManagedDisposition admitted = GuardParity.Classify(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
            Assert.Equal((ManagedStage.Admitted, ChdError.None, 16384U, ChdCodec.Zstd),
                (admitted.Stage, admitted.Error, admitted.Header.HunkBytes, admitted.Header.GetCodec(0)));
            Assert.Equal((ManagedStage.RejectedAtEnvelope, ChdError.InvalidData), Stage(GuardParity.Classify(truncated)));
            Assert.Equal((ManagedStage.RejectedAfterEnvelope, ChdError.InvalidData), Stage(GuardParity.Classify(parent)));
            Assert.Equal((ManagedStage.RejectedAfterEnvelope, ChdError.InvalidData), Stage(GuardParity.Classify(mini)));
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>A codec seed counts only with the required codec and geometry, and only when the harness decoded every hunk
    /// it sampled.</summary>
    [Theory]
    [InlineData("Zstd", 4096U, 64U, 64U, true)]
    [InlineData("Zstd", 4096U, 64U, 63U, false)]
    [InlineData("Zstd", 4096U, 0U, 0U, false)]
    [InlineData("Zstd", 16384U, 64U, 64U, false)]
    [InlineData("Zlib", 4096U, 64U, 64U, false)]
    public void ACodecSeedNeedsItsGeometryAndAFullHarnessDecode(string codec, uint hunkBytes, uint read, uint decoded, bool found)
    {
        var harness = new HarnessDisposition(HarnessStage.Accept, 4096, 4096, 256, read, decoded, "");
        List<(string Input, ChdHeader Header, HarnessDisposition Harness)> admitted = [("fixtures/dvd-zstd-hs4096.chd", Header(ChdCodec.Zstd, 4096), harness)];
        Assert.Equal(found, GuardParity.FindCodecSeed(admitted, Enum.Parse<ChdCodec>(codec), hunkBytes) is not null);
    }

    /// <summary>Every DVD seed the fuzz gate requires is a generated fixture that Moonlark.Libchdr admits with that geometry.</summary>
    [Fact]
    public void EveryRequiredDvdSeedIsAnAdmittedFixture()
    {
        string directory = Path.GetDirectoryName(NativeTestEnvironment.Fixture("dvd-zstd.chd"))!;
        ChdHeader[] admitted = [.. Directory.EnumerateFiles(directory, "*.chd").Select(GuardParity.Classify)
            .Where(disposition => disposition.Stage == ManagedStage.Admitted).Select(disposition => disposition.Header)];
        foreach ((ChdCodec codec, uint hunkBytes) in GuardParity.RequiredDvdSeeds)
            Assert.Contains(admitted, header => GuardParity.IsDvdSeed(header, codec, hunkBytes));
    }

    /// <summary>The comparison's bound limits are the harness's own defines, so a bound rejection can only agree beyond them.</summary>
    [Fact]
    public void ComparisonBoundsEqualTheHarnessDefines()
    {
        string source = File.ReadAllText(Path.Combine(TestRepository.Root, "native", "libchdr", "fuzz", "chd_walk_fuzzer.c"));
        Match hunkBytes = Regex.Match(source, @"^#define MAX_HUNK_BYTES \(1u << ([0-9]+)\)", RegexOptions.Multiline);
        Match hunkCount = Regex.Match(source, @"^#define MAX_HUNK_COUNT ([0-9]+)u\b", RegexOptions.Multiline);
        Assert.True(hunkBytes.Success && hunkCount.Success, "chd_walk_fuzzer.c bound defines changed form; update this test");
        Assert.Equal(GuardParity.HarnessMaxHunkBytes, 1UL << int.Parse(hunkBytes.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(GuardParity.HarnessMaxHunkCount, ulong.Parse(hunkCount.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static ChdHeader Header(ChdCodec codec, uint hunkBytes, uint hunkCount = 1) =>
        new(5, (ulong)hunkBytes * hunkCount, hunkBytes, 2048, hunkCount, codec, ChdCodec.None, ChdCodec.None, ChdCodec.None, default, default,
            default, false);

    private static (ManagedStage, ChdError) Stage(ManagedDisposition disposition) => (disposition.Stage, disposition.Error);

    private static string Write(DirectoryInfo directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
