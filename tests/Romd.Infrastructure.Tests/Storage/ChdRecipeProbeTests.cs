using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Romd.Media.PlayStation;
using Romd.Media.Probe;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdRecipeProbeTests
{
    [Fact]
    public void RandomOffsets_FixedExactWorkload_PreservesAligned64BitGeometry()
    {
        long size = 5314478080;
        long[] first = ChdRecipeChecks.RandomOffsets(size), second = ChdRecipeChecks.RandomOffsets(size);
        first.Length.ShouldBe(11000);
        first.ShouldBe(second);
        first.All(offset => offset >= 0 && offset % 4096 == 0 && offset + 4096 <= size).ShouldBeTrue();
        first.Any(offset => offset > uint.MaxValue).ShouldBeTrue();
        ChdRecipeChecks.RandomOffsets(4096).ShouldAllBe(offset => offset == 0);
        Should.Throw<InvalidOperationException>(() => ChdRecipeChecks.RandomOffsets(4095));
    }

    [Fact]
    public void TimingSummary_NearestRankAndDecimalUnits_DoesNotMutateSamples()
    {
        double[] values = [4, 1, 3, 2];
        ChdRecipeChecks.Quantile(values, .5).ShouldBe(2);
        ChdRecipeChecks.Quantile(values, .95).ShouldBe(4);
        values.ShouldBe([4d, 1d, 3d, 2d]);
        ChdRecipeChecks.DecimalMegabytesPerSecond(100000000, 1).ShouldBe(100);
        ChdRecipeChecks.DecimalMegabytesPerSecond(104857600, 1).ShouldBe(104.8576);
        Should.Throw<InvalidOperationException>(() => ChdRecipeChecks.Quantile([double.NaN], .95));
        Should.Throw<InvalidOperationException>(() => ChdRecipeChecks.DecimalMegabytesPerSecond(1, 0));
    }

    [Theory]
    [InlineData("Size")]
    [InlineData("Crc")]
    [InlineData("Md5")]
    [InlineData("Sha1")]
    [InlineData("Sha256")]
    public void RequireQuartet_EachIndependentMismatch_Rejects(string field)
    {
        var original = new Digests(10000, "12345678", new string('a', 32), new string('b', 40), new string('c', 64));
        Digests changed = field switch
        {
            "Size" => original with { Size = 10001 },
            "Crc" => original with { Crc = new string('d', 8) },
            "Md5" => original with { Md5 = new string('d', 32) },
            "Sha1" => original with { Sha1 = new string('d', 40) },
            _ => original with { Sha256 = new string('d', 64) }
        };
        Should.Throw<InvalidOperationException>(() => ChdRecipeChecks.RequireQuartet(changed, original));
    }

    [Theory]
    [InlineData("{\"Value\":1,\"Value\":2}")]
    [InlineData("{\"Value\":{\"Nested\":1,\"Nested\":2}}")]
    [InlineData("{\"Value\":null}")]
    [InlineData("{\"Unexpected\":1}")]
    [InlineData("{}")]
    public void Parse_InvalidOrAmbiguousJson_Rejects(string json)
    {
        Should.Throw<Exception>(() => ChdRecipeChecks.Parse<RequiredValue>(System.Text.Encoding.UTF8.GetBytes(json)));
    }
    private sealed record RequiredValue(int Value);

    [Fact]
    public async Task RunAsync_TinyNativeCandidate_UsesSixFreshTrialsAndShortTailWithoutMutatingInputs()
    {
        await using var fixture = await Fixture.CreateAsync();
        string before = fixture.ArtifactSha256;
        await fixture.RunAsync();
        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.Report));
        report.RootElement.GetProperty("Status").GetString().ShouldBe("passed");
        var rows = report.RootElement.GetProperty("Rows").EnumerateArray()
            .Where(row => row.TryGetProperty("Kind", out _)).ToArray();
        rows.Length.ShouldBe(6);
        foreach (var row in rows)
        {
            row.GetProperty("ClosedPool").GetProperty("LiveHandles").GetInt32().ShouldBe(0);
            row.GetProperty("ClosedPool").GetProperty("Artifacts").GetInt32().ShouldBe(0);
            row.GetProperty("ClosedCache").GetProperty("LiveRentedBytes").GetInt64().ShouldBe(0);
            row.GetProperty("Pool").GetProperty("OpenedHandles").GetInt32().ShouldBeGreaterThan(0);
            if (row.GetProperty("Kind").GetString() == "random")
            {
                row.GetProperty("Requests").GetInt32().ShouldBe(10000);
                row.GetProperty("LogicalBytes").GetInt64().ShouldBe(40960000);
            }
            else
            {
                row.GetProperty("LogicalBytes").GetInt64().ShouldBe(fixture.Spec.ExpectedSource.Size);
                row.GetProperty("Requests").GetInt32().ShouldBe(8); // Seven complete 4KiB reads and a 2KiB final read.
                row.GetProperty("Pool").GetProperty("SpeculativeStarted").GetInt64().ShouldBeGreaterThan(0);
            }
        }
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Spec.Artifact))).ShouldBe(before);
        (await Digests.FileAsync(fixture.Spec.Iso)).ShouldBe(fixture.Spec.ExpectedSource);
    }

    [Fact]
    public async Task RunAsync_RedumpNameOnlyEntry_UsesExactUniqueEntryName()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Spec.ExpectedSource;
        File.WriteAllText(fixture.Spec.Dat,
            $"<datafile><game name='synthetic'><rom size='{source.Size}' crc='{source.Crc}' md5='{source.Md5}' sha1='{source.Sha1}'/></game></datafile>");
        await fixture.RunAsync();
        fixture.Status().ShouldBe("passed");
    }

    [Fact]
    public async Task RunAsync_RedumpPublicDoctypeNameOnlyEntry_ParsesWithoutExternalFetch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Spec.ExpectedSource;
        File.WriteAllText(fixture.Spec.Dat,
            $"<!DOCTYPE datafile PUBLIC \"-//Logiqx//DTD ROM Management Datafile//EN\" \"http://www.logiqx.com/Dats/datafile.dtd\"><datafile><game name='synthetic'><rom size='{source.Size}' crc='{source.Crc}' md5='{source.Md5}' sha1='{source.Sha1}'/></game></datafile>");
        await fixture.RunAsync();
        fixture.Status().ShouldBe("passed");
    }

    [Theory]
    [InlineData("<!DOCTYPE datafile [<!ENTITY external SYSTEM 'http://127.0.0.1:1/never-fetch'>]>", "&external;")]
    [InlineData("", "&unknown;")]
    public async Task RunAsync_DatEntityReference_RejectsWithoutResolution(string doctype, string entity)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Spec.ExpectedSource;
        File.WriteAllText(fixture.Spec.Dat,
            $"{doctype}<datafile><untrusted>{entity}</untrusted><game name='synthetic'><rom size='{source.Size}' crc='{source.Crc}' md5='{source.Md5}' sha1='{source.Sha1}'/></game></datafile>");
        await Should.ThrowAsync<System.Xml.XmlException>(() => fixture.RunAsync());
        fixture.Status().ShouldBe("failed");
    }

    [Theory]
    [InlineData("-hs", "16384")]
    [InlineData("-c", "zstd")]
    [InlineData("-c", "zlib,zstd")]
    [InlineData("-c", "zlib,zstd,zlib,zstd")]
    public async Task RunAsync_ReceiptRequestedGeometryMismatch_Rejects(string option, string value)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Receipt = fixture.Receipt with { Arguments = [.. fixture.Receipt.Arguments, option, value] };
        fixture.SaveReceipt();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.RunAsync());
        fixture.Status().ShouldBe("failed");
    }

    [Fact]
    public async Task RunAsync_DifferentIndependentLogicalBytes_FailsSeparateReconstruction()
    {
        await using var fixture = await Fixture.CreateAsync();
        byte[] source = File.ReadAllBytes(fixture.Spec.Iso);
        source[100] ^= 1;
        File.WriteAllBytes(fixture.Spec.Iso, source);
        Digests hashes = await Digests.FileAsync(fixture.Spec.Iso);
        fixture.Spec = fixture.Spec with { ExpectedSource = hashes };
        fixture.Receipt = fixture.Receipt with { Source = hashes };
        fixture.SaveReceipt();
        File.WriteAllText(fixture.Spec.Dat,
            $"<datafile><game name='synthetic'><rom size='{hashes.Size}' crc='{hashes.Crc}' md5='{hashes.Md5}' sha1='{hashes.Sha1}'/></game></datafile>");
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.RunAsync());
        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.Report));
        report.RootElement.GetProperty("Status").GetString().ShouldBe("failed");
        report.RootElement.GetProperty("Phase").GetString().ShouldBe("separate-full-reconstructed-quartet");
    }

    [Fact]
    public async Task CompiledCli_TinyNativeCandidate_DispatchesWithoutStartingHost()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SaveSpec();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = fixture.Root };
        start.ArgumentList.Add(typeof(ChdRecipeChecks).Assembly.Location);
        start.ArgumentList.Add("--chd-recipe-probe");
        start.ArgumentList.Add(fixture.SpecPath);
        start.ArgumentList.Add(fixture.Report);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI child did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string stdout = await output, stderr = await error;
            process.ExitCode.ShouldBe(0, stderr + stdout);
            fixture.Status().ShouldBe("passed");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RunAsync_EachCodecSlotMismatch_FailsCheckpoint(int slot)
    {
        await using var fixture = await Fixture.CreateAsync();
        var header = fixture.Spec.ExpectedHeader;
        fixture.Spec = fixture.Spec with
        {
            ExpectedHeader = slot switch
            {
                0 => header with { Codec0 = 0 },
                1 => header with { Codec1 = header.Codec0 },
                2 => header with { Codec2 = header.Codec0 },
                _ => header with { Codec3 = header.Codec0 }
            }
        };
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.RunAsync());
        fixture.Status().ShouldBe("failed");
    }

    [Theory]
    [InlineData("native")]
    [InlineData("tool")]
    [InlineData("receipt-digest")]
    [InlineData("dat")]
    [InlineData("source")]
    public async Task RunAsync_InvalidIndependentInput_FailsClosed(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (failure == "native")
            fixture.Spec = fixture.Spec with { NativeDirectory = Path.Combine(fixture.Root, "missing-native") };
        if (failure == "tool")
            fixture.Spec = fixture.Spec with { ToolDirectory = Path.Combine(fixture.Root, "missing-tool") };
        if (failure == "receipt-digest")
            fixture.Spec = fixture.Spec with { EncodeReceiptSha256 = new string('a', 64) };
        if (failure == "dat")
            File.WriteAllText(fixture.Spec.Dat, "<datafile><game id='wrong'/></datafile>");
        if (failure == "source")
        {
            byte[] bytes = File.ReadAllBytes(fixture.Spec.Iso);
            bytes[0] ^= 1;
            File.WriteAllBytes(fixture.Spec.Iso, bytes);
        }
        await Should.ThrowAsync<Exception>(() => fixture.RunAsync());
        fixture.Status().ShouldBe("failed");
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("elapsed")]
    [InlineData("tool")]
    [InlineData("argv")]
    [InlineData("source")]
    [InlineData("artifact")]
    public async Task RunAsync_ReceiptBindingMismatch_RejectsEvenWithCorrectReceiptDigest(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = fixture.Receipt;
        fixture.Receipt = failure switch
        {
            "exit" => receipt with { ExitCode = 1 },
            "elapsed" => receipt with { ElapsedSeconds = 0 },
            "tool" => receipt with { ToolIdentity = receipt.ToolIdentity with { BinarySha256 = new string('b', 64) } },
            "argv" => receipt with { Arguments = ["createdvd", "-i", fixture.Spec.Iso, "-o", fixture.Spec.Artifact, "-np", "2"] },
            "source" => receipt with { Source = receipt.Source with { Sha1 = new string('b', 40) } },
            _ => receipt with { ArtifactSha256 = new string('b', 64) }
        };
        fixture.SaveReceipt();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.RunAsync());
        fixture.Status().ShouldBe("failed");
    }

    [Theory]
    [InlineData("input")]
    [InlineData("existing")]
    [InlineData("symlink")]
    [InlineData("native-destination")]
    [InlineData("relative")]
    public async Task RunAsync_UnsafeReportDestination_LeavesInputBytesUnchanged(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        byte[] before = File.ReadAllBytes(fixture.Spec.Iso);
        string report = kind switch
        {
            "input" => fixture.Spec.Iso,
            "native-destination" => Path.Combine(fixture.Spec.NativeDirectory, "do-not-create-report.json"),
            "relative" => "relative.json",
            _ => fixture.Report
        };
        if (kind == "existing")
            File.WriteAllText(report, "retained report");
        if (kind == "symlink")
            File.CreateSymbolicLink(report, fixture.Spec.Iso);
        await Should.ThrowAsync<Exception>(() => fixture.RunAsync(report: report));
        File.ReadAllBytes(fixture.Spec.Iso).ShouldBe(before);
        if (kind == "existing")
            File.ReadAllText(report).ShouldBe("retained report");
        if (kind == "native-destination")
            File.Exists(report).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_PreCancelled_RecordsFailureAndDoesNotOpenDecoder()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.RunAsync(cts.Token));
        fixture.Status().ShouldBe("failed");
        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.Report));
        report.RootElement.GetProperty("Rows").GetArrayLength().ShouldBe(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = CreateFixtureDirectory();
        private static string CreateFixtureDirectory()
        {
            string path = Directory.CreateTempSubdirectory("romd-chd-recipe-test-").FullName;
            // Platform temp paths may contain aliases (for example macOS /var -> /private/var).
            // Resolve those parents before exercising the probe's unchanged physical-path policy.
            for (DirectoryInfo? parent = new(path); parent is not null; parent = parent.Parent)
                if (parent.LinkTarget is not null && MediaProbePaths.Contains(parent.FullName, path))
                    path = Path.Combine(parent.ResolveLinkTarget(returnFinalTarget: true)!.FullName,
                        Path.GetRelativePath(parent.FullName, path));
            return Path.GetFullPath(path);
        }
        internal string Report => Path.Combine(Root, "report.json");
        internal string SpecPath => Path.Combine(Root, "spec.json");
        internal ChdRecipeChecks.Specification Spec { get; set; } = null!;
        internal ChdRecipeChecks.EncodeReceipt Receipt { get; set; } = null!;
        internal string ArtifactSha256 => Receipt.ArtifactSha256;
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            try
            {
                var synthetic = SyntheticChdFixture.Create(hunks: 8, finalBytes: 2048);
                string artifact = Path.Combine(fixture.Root, "input.chd"), iso = Path.Combine(fixture.Root, "input.iso"),
                    dat = Path.Combine(fixture.Root, "input.dat"), receiptPath = Path.Combine(fixture.Root, "encode.json");
                await File.WriteAllBytesAsync(artifact, synthetic.Bytes);
                await File.WriteAllBytesAsync(iso, synthetic.Expected(0, (int)synthetic.LogicalBytes));
                Digests source = await Digests.FileAsync(iso), candidate = await Digests.FileAsync(artifact);
                await File.WriteAllTextAsync(dat, $"<datafile><game name='synthetic'><rom size='{source.Size}' crc='{source.Crc}' md5='{source.Md5}' sha1='{source.Sha1}'/></game></datafile>");
                var tool = ChdmanCandidate.FromBuildRecord(NativeTestBuilds.ChdmanDirectory);
                fixture.Receipt = new(iso, artifact, tool.BinaryPath, source, candidate.Size, candidate.Sha256,
                    tool.ExpectedIdentity, ["createdvd", "-i", iso, "-o", artifact, "-np", "1"], 1,
                    DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, 0);
                fixture.Spec = new("synthetic-tests-only", new string('a', 40), "synthetic fixture; unit-test receipt only",
                    artifact, iso, dat, "synthetic", source, NativeTestBuilds.LibchdrDirectory, NativeTestBuilds.ChdmanDirectory,
                    receiptPath, new string('a', 64), new(candidate.Size, (ulong)source.Size, 4096, 2048, 8,
                        BinaryPrimitives.ReadUInt32BigEndian("zlib"u8), 0, 0, 0), 4096);
                fixture.SaveReceipt();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal void SaveReceipt()
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Receipt);
            File.WriteAllBytes(Spec.EncodeReceipt, bytes);
            Spec = Spec with { EncodeReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
        internal Task RunAsync(CancellationToken ct = default, string? report = null)
        {
            SaveSpec();
            return ChdRecipeChecks.RunAsync(["--chd-recipe-probe", SpecPath, report ?? Report], ct);
        }
        internal void SaveSpec() => File.WriteAllBytes(SpecPath, JsonSerializer.SerializeToUtf8Bytes(Spec));
        internal string? Status()
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Report));
            return document.RootElement.GetProperty("Status").GetString();
        }
        public ValueTask DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
