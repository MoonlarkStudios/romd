using System.Reflection;
using System.Text.Json;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;
using Romd.Media.Probe;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class EncodingProbeObservationTests
{
    [Fact]
    public void ChdConfiguration_LegacyBody_OmitsOptionalRecipeAndArtifactMaximum()
    {
        var type = typeof(ChdEncodingChecks).GetNestedType("Configuration", BindingFlags.NonPublic).ShouldNotBeNull();
        const string json = """
            {"Mode":"synthetic","SourceIso":"/source/disc.iso","OriginalHostPath":null,
             "ExpectedSource":{"Size":16777216,"Crc":"00000000","Md5":"11111111111111111111111111111111",
              "Sha1":"1111111111111111111111111111111111111111",
              "Sha256":"1111111111111111111111111111111111111111111111111111111111111111"},
             "ToolDirectory":"/assets/chdman","NativeDirectory":"/assets/libchdr",
             "WorkerPath":"/app/worker.dll","DataDirectory":"/owned/data"}
            """;
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        var configuration = JsonSerializer.Deserialize(json, type, options).ShouldNotBeNull();
        type.GetProperty("RecipeId").ShouldNotBeNull().GetValue(configuration).ShouldBeNull();
        type.GetProperty("MaxArtifactBytes").ShouldNotBeNull().GetValue(configuration).ShouldBeNull();
    }

    [Fact]
    public void SelectRecipe_OmittedOrCanonicalId_PreservesBoundedDefaultAndCanonicalOptIn()
    {
        ChdEncodingChecks.SelectRecipe(null).ShouldBeFalse();
        ChdEncodingChecks.SelectRecipe(ChdDvdCanonicalRecipe.Id).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("chd-dvd-experiment/v1")]
    [InlineData("chd-dvd-canonical/v1")]
    [InlineData("chd-dvd-canonical/v3")]
    [InlineData(" chd-dvd-canonical/v2")]
    [InlineData("chd-dvd-canonical/v2\n")]
    public void SelectRecipe_UnknownId_RefusesClosedSelection(string recipe)
    {
        Should.Throw<InvalidOperationException>(() => ChdEncodingChecks.SelectRecipe(recipe));
    }

    [Fact]
    public void OutputMaximum_CanonicalOrLegacy_UsesDerivedBoundOnlyForCanonical()
    {
        const long source = 16L << 20;
        long expected = ChdDvdOutputBounds.GetMaximumOutputBytes(source, 10L << 30);
        ChdEncodingChecks.OutputMaximum(true, source, 10L << 30, expected).ShouldBe(expected);
        ChdEncodingChecks.OutputMaximum(true, source, 10L << 30, 16L << 30).ShouldBe(expected);
        ChdEncodingChecks.OutputMaximum(false, source, source, source * 2).ShouldBe(source * 2);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(16L << 30 | 1L)]
    [InlineData(1000L)]
    public void OutputMaximum_InvalidOrInsufficientArtifactLimit_RefusesBeforeCapacity(long maximum)
    {
        Should.Throw<InvalidOperationException>(() =>
            ChdEncodingChecks.OutputMaximum(true, 16L << 20, 10L << 30, maximum));
    }

    [Theory]
    [InlineData(2049L)]
    [InlineData(10L << 30)]
    public void OutputMaximum_ZeroDerivedBound_RefusesUnalignedOrUnreservableLogicalSource(long source)
    {
        Should.Throw<InvalidOperationException>(() =>
            ChdEncodingChecks.OutputMaximum(true, source, 10L << 30, 10L << 30));
    }

    [Fact]
    public void OutputMaximum_Cancellation_PropagatesBeforeAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() =>
            ChdEncodingChecks.OutputMaximum(true, 16L << 20, 10L << 30, 10L << 30, cancellation.Token));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("historical-v1-zlib")]
    [InlineData("secondary-codec")]
    [InlineData("hunk")]
    [InlineData("fingerprint")]
    [InlineData("extra-metadata")]
    public void ObservedProfile_ActualScalars_DoesNotPromoteEncoderLabelToCanonical(string mutation)
    {
        var header = new ChdHeaderFacts(10000, 16UL << 20, 16384, 2048, 1024, 0x7a737464, 0, 0, 0);
        var metadata = new ChdDvdMetadataObservation("chd-dvd-metadata/v1", 1, 0x44564420, 1, 1, 0,
            "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406");
        if (mutation == "historical-v1-zlib")
            header = header with { Codec0 = 0x7a6c6962 };
        if (mutation == "secondary-codec")
            header = header with { Codec1 = 0x6c7a6d61 };
        if (mutation == "hunk")
            header = header with { HunkBytes = 32768, HunkCount = 512 };
        if (mutation == "fingerprint")
            metadata = metadata with { NormalizedSha256 = new string('1', 64) };
        if (mutation == "extra-metadata")
            metadata = metadata with { EntryCount = 2 };
        string observed = ChdEncodingChecks.ObservedProfile(5, header, metadata);
        observed.ShouldContain($"codecs={header.Codec0:x8},{header.Codec1:x8},{header.Codec2:x8},{header.Codec3:x8}");
        observed.ShouldContain($"hunk={header.HunkBytes}");
        observed.ShouldContain($"metadata={metadata.Profile}:{metadata.EntryCount}:");
        var classification = ChdEncodingRecipe.Classify(observed);
        if (mutation == "canonical")
            classification.ShouldBe(RomRepresentationProfileClass.Canonical);
        else
            classification.ShouldNotBe(RomRepresentationProfileClass.Canonical);
    }

    [Fact]
    public void ObservedProfile_MissingIndependentMetadata_RefusesIncompleteObservation()
    {
        Should.Throw<InvalidOperationException>(() => ChdEncodingChecks.ObservedProfile(5,
            new(10000, 16UL << 20, 16384, 2048, 1024, 0x7a6c6962, 0, 0, 0), null));
    }

    [Theory]
    [InlineData(16L << 20, 507)]
    [InlineData(4508221440L, 511)]
    public void ReadRanges_FixedFinitePairs_PreservesExactBoundaryAndRepeatInputs(long length, int count)
    {
        var ranges = ChdEncodingChecks.ReadRanges(length);
        ranges.ShouldBe(ChdEncodingChecks.ReadRanges(length));
        ranges.Length.ShouldBe(count);
        ranges.Count(range => range.Kind == "random-4KiB").ShouldBe(250);
        ranges.Count(range => range.Kind == "random-64KiB").ShouldBe(250);
        ranges.Take(500).All(range => range.Offset >= 0 && range.Offset < length && range.Length is 4096 or 65536).ShouldBeTrue();
        ranges.ShouldContain(new ChdEncodingChecks.ReadRange(length - 1, 65536, "truncated-final"));
        ranges.ShouldContain(new ChdEncodingChecks.ReadRange(length, 1, "eof"));
        ranges.ShouldContain(new ChdEncodingChecks.ReadRange(length + 1, 65536, "beyond-eof"));
        ranges.ShouldContain(new ChdEncodingChecks.ReadRange(16383, 4096, "hunk-crossing"));
        if (length > 1L << 32)
        {
            ranges.ShouldContain(new ChdEncodingChecks.ReadRange((1L << 32) - 1, 65536, "4GiB-crossing"));
            ranges.ShouldContain(new ChdEncodingChecks.ReadRange((1L << 32) + 1, 65536, "above-4GiB"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssertRange_WrongBytesOrLength_RejectsTheActualObservation(bool wrongLength)
    {
        byte[] expected = [1, 2, 3], actual = wrongLength ? [1, 2, 3] : [1, 9, 3];
        Should.Throw<InvalidOperationException>(() =>
            ChdEncodingChecks.AssertRange(wrongLength ? 2 : 3, actual, expected));
        ChdEncodingChecks.AssertRange(3, expected, expected);
        ChdEncodingChecks.AssertRange(0, actual, []);
    }

    [Fact]
    public void CountingRawContent_PositionalAndConcurrentReads_RecordsReturnedBytesAndDisposesOwnedSourceOnce()
    {
        var raw = new RawFixture();
        var counters = new ChdEncodingChecks.RawCounters();
        var counted = new ChdEncodingChecks.CountingRawContent(raw, counters);
        counted.Length.ShouldBe(4);
        byte[] bytes = new byte[3];
        counted.ReadAt(1, bytes).ShouldBe(3);
        bytes.ShouldBe(new byte[] { 2, 3, 4 });
        counted.ReadAt(4, bytes).ShouldBe(0);
        Parallel.For(0, 10, _ =>
        {
            byte[] destination = new byte[2];
            counted.ReadAt(0, destination).ShouldBe(2);
            destination.ShouldBe(new byte[] { 1, 2 });
        });
        counted.Dispose();
        counted.Dispose();
        counters.Snapshot().ShouldBe(new(12, 23, 1, 1));
        raw.DisposeCalls.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => counted.ReadAt(0, bytes));
        counters.Snapshot().ShouldBe(new(12, 23, 1, 1));
    }

    [Fact]
    public void CountingRawContent_ReadFailure_PropagatesWithoutInventingReturnedBytes()
    {
        var raw = new RawFixture { Fail = true };
        var counters = new ChdEncodingChecks.RawCounters();
        using var counted = new ChdEncodingChecks.CountingRawContent(raw, counters);
        Should.Throw<IOException>(() => counted.ReadAt(0, new byte[2]));
        counters.Snapshot().ShouldBe(new(1, 0, 1, 0));
    }

    private sealed class RawFixture : ISynchronousRawContent
    {
        public long Length => 4;
        public bool Fail { get; init; }
        public int DisposeCalls { get; private set; }
        public int ReadAt(long offset, Span<byte> destination)
        {
            if (Fail)
                throw new IOException("Synthetic raw callback failure.");
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            byte[] bytes = [1, 2, 3, 4];
            if (offset >= Length)
                return 0;
            int count = (int)Math.Min(destination.Length, Length - offset);
            bytes.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }
        public void Dispose() => DisposeCalls++;
    }

    [Fact]
    public void IsCapacityOnly_EmptyOrdinaryRun_CannotReportSuccess()
    {
        Should.Throw<InvalidOperationException>(() => EncodingChecks.IsCapacityOnly(0, false, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative.iso")]
    public void IsCapacityOnly_OptInWithoutAbsoluteRecovery_RejectsBeforeChildWork(string? recoveryIso)
    {
        Should.Throw<InvalidOperationException>(() => EncodingChecks.IsCapacityOnly(0, true, recoveryIso));
    }

    [Fact]
    public void IsCapacityOnly_ExplicitTinyOrCombinedPhase_DistinguishesScope()
    {
        string path = Path.Combine(Path.GetTempPath(), "recovery.iso");
        EncodingChecks.IsCapacityOnly(0, true, path).ShouldBeTrue();
        EncodingChecks.IsCapacityOnly(1, true, path).ShouldBeFalse();
        EncodingChecks.IsCapacityOnly(1, false, null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_LegacyOrExplicitCapacitySelection_DefaultsToNoCapacityClaim(bool optIn)
    {
        var type = typeof(EncodingChecks).GetNestedType("Configuration", BindingFlags.NonPublic).ShouldNotBeNull();
        string json = optIn ? "{\"Fixtures\":[],\"QualifyDefaultProcessingCapacity\":true}" : "{\"Fixtures\":[]}";
        var configuration = JsonSerializer.Deserialize(json, type).ShouldNotBeNull();
        type.GetProperty("QualifyDefaultProcessingCapacity").ShouldNotBeNull().GetValue(configuration).ShouldBe(optIn);
    }

    [Fact]
    public void ReadOutputLength_PayloadOrItsDirectoryDisappeared_ReturnsAbsentOutput()
    {
        string directory = Directory.CreateTempSubdirectory("romd-observation-race-").FullName;
        string output = Path.Combine(directory, "output.data");
        try
        {
            File.WriteAllBytes(output, [1, 2, 3]);
            EncodingChecks.ReadOutputLength(output).ShouldBe(3);
            File.Delete(output);
            EncodingChecks.ReadOutputLength(output).ShouldBe(0);
            Directory.Delete(directory);
            EncodingChecks.ReadOutputLength(output).ShouldBe(0);
            EncodingChecks.ReadOutputLength(null).ShouldBe(0);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("/usr/bin/nice")]
    [InlineData("/usr/bin/prlimit")]
    public void IsEncodingRuntime_LauncherContainsWorkerArguments_DoesNotSamplePreRuntimePriority(string launcher)
    {
        EncodingObservations.IsEncodingRuntime([launcher, "/usr/bin/dotnet", "/app/media-worker/Romd.Media.Worker.dll",
            "encode", "/staging/source.data", "/staging/output.data"], "/staging/source.data").ShouldBeFalse();
    }

    [Fact]
    public void IsEncodingRuntime_ActualRuntimeAndExactSource_AcceptsOnlyTheOwnedEncodingChild()
    {
        string[] arguments = ["/usr/share/dotnet/dotnet", "/app/media-worker/Romd.Media.Worker.dll", "encode",
            "/staging/source.data", "/staging/output.data"];
        EncodingObservations.IsEncodingRuntime(arguments, "/staging/source.data").ShouldBeTrue();
        EncodingObservations.IsEncodingRuntime(arguments, "/staging/other.data").ShouldBeFalse();
    }

    [Fact]
    public void ReadThreadStat_ThreadNameContainsSpacesAndParentheses_ReportsKernelNiceAndCpuTicks()
    {
        const string stat = "123 (managed worker (encode)) R 1 123 123 0 -1 4194304 7 0 0 0 223 17 0 0 30 10 7 0 999 20000 45";
        EncodingObservations.ReadThreadStat(123, stat).ShouldBe(new EncodingThreadObservation(123, 1, 10, 10, 240, 240));
    }

    [Fact]
    public void ReadThreadStat_NormalPriorityCpuThread_ReportsZeroRatherThanCanonicalPriority()
    {
        const string stat = "124 (.NET TP Worker) S 1 123 123 0 -1 4194304 7 0 0 0 450 20 0 0 20 0 7 0 999 20000 45";
        EncodingObservations.ReadThreadStat(124, stat).ShouldBe(new EncodingThreadObservation(124, 1, 0, 0, 470, 470));
    }

    [Fact]
    public async Task RunAsync_CanonicalFfxMissingSource_ReachesSourceFailureAfterValidStagingSetup()
    {
        string temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        string root = Path.Combine(temporaryRoot, "romd-chd-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string directory in new[] { "control", "reports", "inputs", "assets" })
                Directory.CreateDirectory(Path.Combine(root, directory));
            string configurationPath = Path.Combine(root, "control", "configuration.json");
            string reportPath = Path.Combine(root, "reports", "report.json");
            string sourcePath = Path.Combine(root, "inputs", "missing.iso");
            string dataDirectory = Path.Combine(root, "owned-data");
            // Synthetic build records and a synthetic fixture root: never the real fixtures or real native builds.
            string toolDirectory = NativeTestBuilds.WriteChdmanBuild(Path.Combine(root, "assets", "chdman"), [1],
                NativeTestBuilds.Rid, ChdmanPin.Tag, ChdmanPin.SourceSha256);
            string nativeDirectory = NativeTestBuilds.WriteLibchdrBuild(Path.Combine(root, "assets", "libchdr"), [1],
                NativeTestBuilds.Rid, ChdNativeCandidate.CompiledUpstreamCommit);
            string fixtureRoot = Path.Combine(root, "fixtures");
            byte[] configuration = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Mode = "ffx",
                SourceIso = sourcePath,
                OriginalHostPath = Path.Combine(fixtureRoot, "ps2", "Final Fantasy X (USA).iso"),
                ExpectedSource = new
                {
                    Size = 4508221440L,
                    Crc = "9ad80720",
                    Md5 = "57998b60010b7583c423d5b2142ad1a3",
                    Sha1 = "42ab110b759e600365b99612e76cf51ef3e95901",
                    Sha256 = "31d59977ef19f0ff7018d2787d18beaa569331138c0e5f351e0bdac387a2a031"
                },
                ToolDirectory = toolDirectory,
                NativeDirectory = nativeDirectory,
                WorkerPath = Path.Combine(root, "assets", "worker.dll"),
                DataDirectory = dataDirectory,
                RecipeId = ChdDvdCanonicalRecipe.Id,
                MaxArtifactBytes = 10L << 30
            });
            await File.WriteAllBytesAsync(configurationPath, configuration);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string[] arguments = ["--chd-encoding-probe", configurationPath, reportPath];
            File.Exists(sourcePath).ShouldBeFalse();
            if (!OperatingSystem.IsLinux())
            {
                var refusal = await Should.ThrowAsync<InvalidOperationException>(() => ChdEncodingChecks.RunAsync(arguments, deadline.Token, () => fixtureRoot));
                refusal.Message.ShouldBe("FAILED: genuine CHD encoding probe uses the Linux route");
                File.Exists(reportPath).ShouldBeFalse();
                Directory.Exists(dataDirectory).ShouldBeFalse();
                return;
            }

            var error = await Should.ThrowAsync<IOException>(() => ChdEncodingChecks.RunAsync(arguments, deadline.Token, () => fixtureRoot));
            error.Message.ShouldBe("The source descriptor could not be opened.");
            File.Exists(reportPath).ShouldBeTrue();
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(reportPath, deadline.Token));
            var observed = report.RootElement;
            observed.GetProperty("Status").GetString().ShouldBe("failed");
            string savedError = observed.GetProperty("Error").GetString() ?? throw new InvalidDataException("Missing probe failure.");
            savedError.ShouldContain("System.IO.IOException: The source descriptor could not be opened.");
            savedError.ShouldContain("Romd.Media.Processes.NativeFileLease.OpenRegularRead");
            observed.GetProperty("SourceBefore").ValueKind.ShouldBe(JsonValueKind.Null);
            observed.GetProperty("SourceAfter").ValueKind.ShouldBe(JsonValueKind.Null);
            observed.GetProperty("Rows").GetArrayLength().ShouldBe(0);
            observed.GetProperty("InputMaximumBytes").GetInt64().ShouldBe(10L << 30);
            observed.GetProperty("ConfiguredArtifactMaximumBytes").GetInt64().ShouldBe(10L << 30);
            observed.GetProperty("OutputMaximumBytes").GetInt64().ShouldBe(4509700598L);
            var limits = observed.GetProperty("Limits");
            limits.GetProperty("MaxLogicalBytes").GetInt64().ShouldBe(10L << 30);
            limits.GetProperty("MaxArtifactBytes").GetInt64().ShouldBe(10L << 30);
            var staging = observed.GetProperty("StagingLimits");
            staging.GetProperty("MaximumFiles").GetInt32().ShouldBe(2);
            staging.GetProperty("MaximumBytes").GetInt64().ShouldBe(9017922038L);
            Directory.Exists(Path.Combine(dataDirectory, "temp", "staging-v1")).ShouldBeTrue();
            Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).ShouldBeEmpty();
            File.Exists(sourcePath).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
