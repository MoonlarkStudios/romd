using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Processes;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Media.Tests.PlayStation;

// Unsupported hosts assert LinuxRequired. Supported Linux requires frozen prerequisites, never skips.
public sealed class ChdDvdEncodingServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "romd-dvd-encode-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Source => Path.Combine(_directory, "source image.iso");
    private string Output => Path.Combine(_directory, "reserved artifact.chd");
    private const int SourceBytes = 32768;
    private const long OutputCap = 8192;

    [Fact]
    public async Task Portable_CancelledBeforeMissingSource_ThrowsCancellation()
    {
        await Should.ThrowAsync<OperationCanceledException>(() => Encode(MissingCandidate(), ct: new CancellationToken(true)));
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(3, 32768, 8192)]
    [InlineData(32768, 128, 8192)]
    [InlineData(32768, 32768, 123)]
    public async Task Portable_InvalidBudgets_FailBeforeMissingTool(long artifact, long logical, long output)
    {
        CreateSource();
        AssertRejected(await Encode(MissingCandidate(), artifact: artifact, logical: logical, output: output),
            MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.InvalidArguments);
    }

    [Fact]
    public async Task Portable_MissingSource_FailsBeforeMissingTool()
    {
        AssertRejected(await Encode(MissingCandidate()), MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceIo);
    }

    [Fact]
    public async Task Portable_ExpectedLengthMismatch_FailsBeforeMissingTool()
    {
        CreateSource();
        AssertRejected(await Encode(MissingCandidate(), expectedBytes: SourceBytes + 2048),
            MediaWorkerStatus.Malformed, ChdDvdEncodingCause.SourceLengthMismatch);
    }

    [Fact]
    public async Task Portable_ExpectedSourceHashMismatch_FailsBeforeMissingTool()
    {
        CreateSource();
        AssertRejected(await Encode(MissingCandidate(), expectedHash: new string('a', 64)),
            MediaWorkerStatus.Malformed, ChdDvdEncodingCause.SourceHashMismatch);
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public async Task Portable_InputArtifactOverLimit_FailsBeforeMissingTool()
    {
        CreateSource();
        AssertRejected(await Encode(MissingCandidate(), artifact: SourceBytes - 1),
            MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.LimitExceeded);
    }

    [Fact]
    public async Task Portable_SourceLogicalOverLimit_FailsBeforeMissingTool()
    {
        CreateSource();
        AssertRejected(await Encode(MissingCandidate(), logical: SourceBytes - 1),
            MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.LimitExceeded);
    }

    [Fact]
    public async Task Portable_NonDvdAlignedSource_FailsBeforeMissingTool()
    {
        CreateSource(bytes: SourceBytes - 1);
        AssertRejected(await Encode(MissingCandidate(), expectedBytes: SourceBytes - 1),
            MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource);
    }

    [Theory]
    [InlineData("chd")]
    [InlineData("rvz")]
    [InlineData("cd-sync")]
    public async Task Portable_RecognizedContainerOrCd_FailsBeforeMissingTool(string signature)
    {
        CreateSource();
        byte[] bytes = File.ReadAllBytes(Source);
        (signature switch
        {
            "chd" => "MComprHD"u8.ToArray(),
            "rvz" => new byte[] { 0x52, 0x56, 0x5a, 1 },
            _ => new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0 }
        }).CopyTo(bytes, 0);
        File.WriteAllBytes(Source, bytes);
        AssertRejected(await Encode(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource);
    }

    [Fact]
    public async Task Portable_PreexistingOutput_IsPreservedBeforeMissingTool()
    {
        CreateSource();
        byte[] original = "owned by another operation"u8.ToArray();
        File.WriteAllBytes(Output, original);
        AssertRejected(await Encode(MissingCandidate()), MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.OutputExists);
        File.ReadAllBytes(Output).ShouldBe(original);
    }

    [Fact]
    public async Task Portable_OutputAliasesSource_IsRejectedWithoutOverwrite()
    {
        CreateSource();
        byte[] original = File.ReadAllBytes(Source);
        AssertRejected(await ChdDvdEncodingService.EncodeAsync(Source, Source, MissingCandidate(), SourceBytes, Sha256(original),
            SourceBytes, SourceBytes, OutputCap, "unix-nice", 10, -1), MediaWorkerStatus.Malformed, ChdDvdEncodingCause.InvalidPath);
        File.ReadAllBytes(Source).ShouldBe(original);
    }

    [Fact]
    public async Task Portable_LinkedSource_IsRejectedBeforeMissingTool()
    {
        CreateSource();
        string link = Path.Combine(_directory, "linked.iso");
        File.CreateSymbolicLink(link, Source);
        AssertRejected(await ChdDvdEncodingService.EncodeAsync(link, Output, MissingCandidate(), SourceBytes, Sha256(File.ReadAllBytes(Source)),
            SourceBytes, SourceBytes, OutputCap, "unix-nice", 10, -1), MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.UnsafePath);
    }

    [Fact]
    public async Task Linux_SyntheticSuccess_MeasuresRawArtifactAndSourceWithoutIntegrityClaim()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource();
        using var lease = Lease();
        ChdmanCandidate candidate = FixtureCandidate();
        var outcome = await Encode(candidate, lease.Descriptor);
        outcome.Status.ShouldBe(MediaWorkerStatus.Encoded);
        outcome.Rejection.ShouldBeNull();
        var artifact = outcome.Artifact.ShouldNotBeNull();
        byte[] output = File.ReadAllBytes(Output);
        artifact.SourceBytes.ShouldBe(SourceBytes);
        artifact.SourceSha256.ShouldBe(Sha256(File.ReadAllBytes(Source)));
        artifact.ArtifactBytes.ShouldBe(output.Length);
        artifact.ArtifactSha256.ShouldBe(Sha256(output));
        artifact.ArtifactMd5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(output)));
        artifact.ArtifactSha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(output)));
        artifact.LogicalBytes.ShouldBe(SourceBytes);
        artifact.Header.LogicalBytes.ShouldBe((ulong)SourceBytes);
        artifact.Header.UnitBytes.ShouldBe(2048U);
        artifact.Header.HunkBytes.ShouldBe(16384U);
        artifact.Header.HunkCount.ShouldBe(2U);
        artifact.Header.Codec0.ShouldBe(0x7a737464U);
        artifact.Header.Codec1.ShouldBe(0U);
        artifact.Header.Codec2.ShouldBe(0U);
        artifact.Header.Codec3.ShouldBe(0U);
        artifact.RecipeVersion.ShouldBe("chd-dvd-canonical/v2");
        artifact.EncodingProfile.ShouldBe(ChdDvdCanonicalRecipe.EncoderProfile);
        outcome.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.ToolIdentity!.MameTag.ShouldBe(ChdmanPin.Tag);
        AssertJoined(outcome);
    }

    [Fact]
    public async Task Linux_ShortTool_RunsItsSealedVerifiedImageWithoutObservationAndExactJoins()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(23);
        using var lease = Lease();
        var candidate = FixtureCandidate();
        for (int invocation = 0; invocation < 8; invocation++)
        {
            File.Delete(Output);
            var result = await Encode(candidate, lease.Descriptor);
            result.Status.ShouldBe(MediaWorkerStatus.Encoded, Describe(result.Rejection, result.Diagnostics));
            result.Rejection.ShouldBeNull();
            result.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
            result.Artifact.ShouldNotBeNull();
            AssertJoined(result);
        }
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("link")]
    public async Task Linux_BinaryReplacedAfterVerification_IsRejectedFromItsSealedImageBeforeAnyProcess(string replacement)
    {
        // The helper asserts the non-Linux refusal; the explicit check lets the platform analyzer see this Linux-only route.
        if (await AssertUnsupportedPlatformAsync() || !OperatingSystem.IsLinux())
            return;
        CreateSource();
        using var lease = Lease();
        ChdmanCandidate candidate = FixtureCandidate();
        byte[] verified = File.ReadAllBytes(candidate.BinaryPath);
        string moved = candidate.BinaryPath + ".verified";
        File.Move(candidate.BinaryPath, moved);
        if (replacement == "link")
            File.CreateSymbolicLink(candidate.BinaryPath, moved);
        else
        {
            byte[] changed = [.. verified];
            changed[^1] ^= 0xff;
            File.WriteAllBytes(candidate.BinaryPath, changed);
            File.SetUnixFileMode(candidate.BinaryPath, File.GetUnixFileMode(moved));
        }
        var run = await ChdmanToolRunner.RunAsync(candidate, ChdDvdCanonicalRecipe.ChdmanArguments(Source, Output), OutputCap,
            lease.Descriptor, ChdmanToolRunner.CurrentExpectation(), CancellationToken.None);
        run.Status.ShouldBe(MediaWorkerStatus.IoFailure);
        run.Rejection.ShouldBe(new(ChdDvdEncodingStage.ToolImage, ChdDvdEncodingCause.ToolImageMismatch, candidate.BinaryPath));
        run.Diagnostics.ShouldBeNull();
        File.Exists(Source + ".pid").ShouldBeFalse("no process may start from an image that differs from its pinned identity");
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public async Task Linux_ToolNonzero_HasNoArtifactMetadataAndJoinedStatus()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(1);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        AssertRejected(result, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.ToolExitFailure);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(23);
        AssertJoined(result);
    }

    [Theory]
    [InlineData(9, "1 1", 979_999L, false)]
    [InlineData(9, "1 1", 980_000L, true)]
    [InlineData(24, "1 2", 979_999L, false)]
    [InlineData(24, "1 2", 980_000L, true)]
    [InlineData(9, "2 2", 1_979_999L, false)]
    [InlineData(9, "2 2", 1_980_000L, true)]
    [InlineData(24, "2 3", 1_979_999L, false)]
    [InlineData(24, "2 3", 1_980_000L, true)]
    [InlineData(0, "1 1", 2_000_000L, false)]
    [InlineData(15, "1 1", 2_000_000L, false)]
    [InlineData(137, "1 1", 2_000_000L, false)]
    [InlineData(9, "unlimited unlimited", 2_000_000L, false)]
    [InlineData(9, "", 2_000_000L, false)]
    [InlineData(9, "0 1", 2_000_000L, false)]
    [InlineData(24, "-1 2", 2_000_000L, false)]
    public void Portable_CpuBudgetEvidence_RequiresSignalFiniteLimitAndMeasuredCutoff(
        int signal, string cpu, long microseconds, bool expected) =>
        ChdmanToolRunner.HasCpuBudgetEvidence(signal, cpu, microseconds).ShouldBe(expected);

    [Theory]
    [InlineData(17, 152, "1:2", "1 2")]
    [InlineData(18, 137, "1:1", "1 1")]
    public async Task Linux_FirstCpuLimit_EnforcesSignalAndClassifiesJoinedEvidence(int mode, int exitCode, string cpu, string expectedCpu)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        var result = await RunCpuFixtureAsync(mode, cpu, expectedCpu);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(exitCode);
        result.Diagnostics.CpuMicroseconds.ShouldBeGreaterThan(0);
        AssertCpuLimits(expectedCpu);
        // The owned final PROF clock is the clock that enforces the inherited limit.
        result.Diagnostics.CpuMicroseconds.ShouldBeGreaterThanOrEqualTo(980_000);
        AssertRejected(result, MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.CpuBudgetExceeded);
        AssertJoined(result);
    }

    [Theory]
    [InlineData(20, 152)]
    [InlineData(21, 137)]
    public async Task Linux_KernelKillWithMeasuredCpuEvidence_IsLimitExceeded(int mode, int exitCode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        var result = await RunCpuFixtureAsync(mode, "1:unlimited", "1 unlimited");
        AssertRejected(result, MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.CpuBudgetExceeded);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(exitCode);
        result.Diagnostics.CpuMicroseconds.ShouldBeGreaterThanOrEqualTo(1_250_000);
        string trace = Encoding.UTF8.GetString(result.Diagnostics.StandardOutputTail);
        trace.ShouldContain("cpu-first-warning\n");
        trace.ShouldContain("cpu-evidence-ready\n");
        AssertCpuLimits("1 unlimited");
        AssertJoined(result);
    }

    private async Task<ChdDvdEncodingOutcome> RunCpuFixtureAsync(int mode, string cpu, string expectedCpu)
    {
        CreateSource(mode);
        using var lease = Lease();
        var candidate = FixtureCandidate();
        var start = new ProcessStartInfo("/usr/bin/prlimit") { UseShellExecute = false };
        start.Environment.Clear();
        foreach (string argument in new[] { "--cpu=" + cpu, "--fsize=8192:8192", "--", candidate.BinaryPath,
            "createdvd", "-i", Source, "-o", Output, "-hs", "16384", "-c", "zstd", "-np", "1" })
            start.ArgumentList.Add(argument);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // A direct run with an extra --cpu limit; sealed launch construction is covered separately.
        var run = await ChdmanToolRunner.RunPreparedAsync(start, lease.Descriptor, [], candidate,
            ChdmanToolRunner.CurrentExpectation() with { Cpu = expectedCpu }, deadline.Token);
        var diagnostics = run.Diagnostics.ShouldNotBeNull();
        output.WriteLine($"Inherited CPU limits: {expectedCpu}; exit: {diagnostics.ExitCode}; joined CPU: {diagnostics.CpuMicroseconds} us");
        return new(run.Status, Rejection: run.Rejection, Diagnostics: run.Diagnostics);
    }

    private void AssertCpuLimits(string expectedCpu)
    {
        using var facts = JsonDocument.Parse(File.ReadAllBytes(Source + ".facts"));
        string[] limits = expectedCpu.Split(' ');
        facts.RootElement.GetProperty("CpuSoft").GetUInt64().ShouldBe(LimitNumber(limits[0]));
        facts.RootElement.GetProperty("CpuHard").GetUInt64().ShouldBe(LimitNumber(limits[1]));
    }

    [Theory]
    [InlineData(19, 137)]
    [InlineData(24, 152)]
    public async Task Linux_ExternalCpuOrKillSignal_IsMalformedToolExitFailure(int mode, int exitCode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(mode);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        AssertRejected(result, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.ToolExitFailure);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(exitCode);
        result.Diagnostics.CpuMicroseconds.ShouldBeLessThan(980_000);
        AssertJoined(result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Linux_ExitZeroInvalidArtifact_IsRejected(int mode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(mode);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        AssertRejected(result, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.InvalidOutputHeader);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(0);
        AssertJoined(result);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(14)]
    public async Task Linux_OrdinaryProgressBeyondTailBudget_RemainsBoundedAndEncodes(int mode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(mode);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        result.Status.ShouldBe(MediaWorkerStatus.Encoded);
        var diagnostics = result.Diagnostics.ShouldNotBeNull();
        (diagnostics.StandardOutputBytes + diagnostics.StandardErrorBytes).ShouldBeGreaterThan(65536L);
        diagnostics.StandardOutputTail.Length.ShouldBeLessThanOrEqualTo(4096);
        diagnostics.StandardErrorTail.Length.ShouldBeLessThanOrEqualTo(4096);
        if (mode == 14)
            Encoding.UTF8.GetString(diagnostics.StandardErrorTail).ShouldEndWith("stderr-tail-marker");
        AssertJoined(result);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Linux_PipeFlood_StopsAndJoinsWithBoundedTails(int mode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(mode);
        using var lease = Lease();
        ChdmanCandidate candidate = FixtureCandidate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Encode(candidate, lease.Descriptor, ct: deadline.Token);
        AssertRejected(result, MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.DiagnosticLimitExceeded);
        var diagnostics = result.Diagnostics.ShouldNotBeNull();
        diagnostics.StandardOutputTail.Length.ShouldBeLessThanOrEqualTo(4096);
        diagnostics.StandardErrorTail.Length.ShouldBeLessThanOrEqualTo(4096);
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_OutputCapCrossing_IsBoundedDuringWriteAndDirectJoined()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(8);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        result.Artifact.ShouldBeNull();
        result.Status.ShouldNotBe(MediaWorkerStatus.Encoded);
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(153); // Actual SIGXFSZ, not a final-size inference.
        new FileInfo(Output).Length.ShouldBe(OutputCap);
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_CancelledLiveTool_RetainsLeaseThroughExactDirectJoin()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(9);
        ChdmanCandidate candidate = FixtureCandidate();
        var lease = Lease();
        var identity = lease.Identity;
        using var cancel = new CancellationTokenSource();
        Task<ChdDvdEncodingOutcome>? run = null;
        int pid = -1;
        try
        {
            run = Encode(candidate, lease.Descriptor, ct: cancel.Token);
            pid = await WaitForPidAsync();
            lease.Dispose();
            using (var denied = NativeFileLease.TryAcquire(Path.Combine(_directory, "processing.lease")))
                denied.ShouldBeNull();
            cancel.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => run!);
            Directory.Exists($"/proc/{pid}").ShouldBeFalse();
            using var acquired = NativeFileLease.TryAcquire(Path.Combine(_directory, "processing.lease")).ShouldNotBeNull();
            acquired.Identity.ShouldBe(identity);
        }
        finally
        {
            cancel.Cancel();
            if (run is not null)
            {
                try
                { await run; }
                catch (OperationCanceledException) { }
            }
            lease.Dispose();
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(15)]
    public async Task Linux_SourceChangedDuringTool_IsRejectedWithNoArtifactMetadata(int mode)
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(mode);
        if (mode == 15)
        {
            // Same length, different bytes: only the named-path re-hash can see the replaced name.
            byte[] replacement = File.ReadAllBytes(Source);
            replacement[256] ^= 1;
            File.WriteAllBytes(Source + ".replacement", replacement);
        }
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        AssertRejected(result, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged);
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_EncodeAsync_HeldInodeChangedBehindUntouchedReplacement_RejectsWithoutArtifactMetadata()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(16);
        byte[] original = File.ReadAllBytes(Source);
        File.WriteAllBytes(Source + ".replacement", original);
        using var held = File.OpenHandle(Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lease = Lease();

        var result = await Encode(FixtureCandidate(), lease.Descriptor);

        AssertHeldInodeMutation(held, original);
        AssertRejected(result, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged);
        AssertJoined(result);
        AssertExactSuccessfulTool(result.Diagnostics.ShouldNotBeNull());
    }

    [Fact]
    public async Task Linux_ActualPayload_InheritsExactArgumentsEmptyEnvironmentGroupPriorityAndLimits()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource();
        using var lease = Lease();
        string nonce = Guid.NewGuid().ToString("N");
        lease.Bind(nonce);
        int originalFlags = NativeFileLease.Fcntl(lease.Descriptor, 3, 0);
        var expected = InheritedExpectation();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        result.Status.ShouldBe(MediaWorkerStatus.Encoded);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Source + ".facts"));
        JsonElement facts = document.RootElement;
        facts.GetProperty("Pid").GetInt32().ShouldBe(result.Diagnostics.ShouldNotBeNull().ProcessId);
        facts.GetProperty("GroupId").GetInt32().ShouldBe(expected.GroupId);
        facts.GetProperty("Nice").GetInt32().ShouldBe(expected.Nice);
        facts.GetProperty("Nonce").GetString().ShouldBe(nonce);
        facts.GetProperty("LeaseFlags").GetInt32().ShouldBe(0);
        AssertSealedLaunch(facts);
        facts.GetProperty("EnvironmentCount").GetInt32().ShouldBe(0);
        facts.GetProperty("FlockDenied").GetInt32().ShouldBe(1);
        facts.GetProperty("StatusFlags").GetInt32().ShouldBe(originalFlags | 0x800);
        NativeFileLease.Fcntl(lease.Descriptor, 3, 0).ShouldBe(originalFlags | 0x800);
        facts.GetProperty("FsizeSoft").GetUInt64().ShouldBe((ulong)OutputCap);
        facts.GetProperty("FsizeHard").GetUInt64().ShouldBe((ulong)OutputCap);
        foreach (var entry in new[] { ("AddressSpace", expected.AddressSpace), ("Data", expected.Data), ("Cpu", expected.Cpu), ("Core", expected.Core) })
        {
            string[] values = entry.Item2.Split(' ');
            facts.GetProperty(entry.Item1 + "Soft").GetUInt64().ShouldBe(LimitNumber(values[0]));
            facts.GetProperty(entry.Item1 + "Hard").GetUInt64().ShouldBe(LimitNumber(values[1]));
        }
        // Root compares these actual ABI observations before supplying an S_IFREG production guard.
        facts.GetProperty("StatSize").GetInt32().ShouldBeGreaterThan(0);
        facts.GetProperty("StatModeOffset").GetInt32().ShouldBeLessThan(facts.GetProperty("StatSize").GetInt32());
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_ToolCreatesLinkedOutput_IsRejectedWithoutDeletingSourceOrPartialOutput()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(12);
        byte[] original = File.ReadAllBytes(Source);
        using var lease = Lease();
        var result = await Encode(FixtureCandidate(), lease.Descriptor);
        AssertRejected(result, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.UnsafePath);
        new FileInfo(Output).LinkTarget.ShouldNotBeNull();
        File.ReadAllBytes(Source).ShouldBe(original);
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_ToolCreatesFifoOutput_RejectsWithoutBlockingOrDeletingIt()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(13);
        using var lease = Lease();
        ChdmanCandidate candidate = FixtureCandidate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Encode(candidate, lease.Descriptor, ct: deadline.Token);
        AssertRejected(result, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.UnsafePath);
        Directory.EnumerateFileSystemEntries(_directory).ShouldContain(Output);
        AssertJoined(result);
    }

    [Fact]
    public async Task Linux_PairedNativeWriterCaps_IsolatesActualDuringWriteEnforcement()
    {
        if (await AssertUnsupportedPlatformAsync())
            return;
        CreateSource(8);
        ChdmanCandidate candidate = FixtureCandidate();
        using var lease = Lease();
        foreach (long cap in new[] { 8192L, 16384L })
        {
            string output = Path.Combine(_directory, "native-cap-" + cap.ToString(CultureInfo.InvariantCulture) + ".chd");
            var start = new ProcessStartInfo("/usr/bin/prlimit") { UseShellExecute = false };
            start.Environment.Clear();
            foreach (string argument in new[] { "--fsize=" + cap.ToString(CultureInfo.InvariantCulture) + ":" + cap.ToString(CultureInfo.InvariantCulture),
                "--", candidate.BinaryPath, "createdvd", "-i", Source, "-o", output, "-hs", "16384", "-c", "zstd", "-np", "1" })
                start.ArgumentList.Add(argument);
            using PosixMediaChild child = PosixMediaChild.StartTool(start, lease.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<byte[]> stdout = ReadBoundedAsync(child.Output, deadline.Token), stderr = ReadBoundedAsync(child.Error, deadline.Token);
            try
            {
                child.Input.Dispose();
                await child.WaitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr);
                child.ExitCode.ShouldBe(cap == 8192 ? 153 : 0);
                new FileInfo(output).Length.ShouldBe(cap == 8192 ? 8192 : 8193);
                using var facts = JsonDocument.Parse(File.ReadAllBytes(Source + ".facts"));
                facts.RootElement.GetProperty("Pid").GetInt32().ShouldBe(child.Id);
                facts.RootElement.GetProperty("FsizeSoft").GetUInt64().ShouldBe((ulong)cap);
                facts.RootElement.GetProperty("FsizeHard").GetUInt64().ShouldBe((ulong)cap);
            }
            finally { await child.TerminateAndWaitAsync(); await Task.WhenAll(stdout, stderr); }
            Directory.Exists($"/proc/{child.Id}").ShouldBeFalse();
        }
        // Both raw test outputs remain owned until each direct wait and pipe-drain join completes.
    }

    [Fact]
    public async Task ExtractAsync_PreCanceled_PreservesExactTokenWithoutFilesOrTool()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await Extract(MissingCandidate(), ct: canceled.Token);
            throw new InvalidOperationException("Canceled extraction must throw.");
        }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(canceled.Token); }
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sourceBytes", MediaWorkerStatus.Malformed, ChdDvdEncodingCause.SourceLengthMismatch)]
    [InlineData("sourceHash", MediaWorkerStatus.Malformed, ChdDvdEncodingCause.SourceHashMismatch)]
    [InlineData("sourceCap", MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.LimitExceeded)]
    [InlineData("logicalCap", MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.LimitExceeded)]
    [InlineData("outputCap", MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.LimitExceeded)]
    [InlineData("logicalBinding", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    [InlineData("version", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    [InlineData("parent", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    [InlineData("cdCodec", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    [InlineData("unit", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    [InlineData("hunk", MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.UnsupportedSource)]
    public async Task ExtractAsync_WrongIdentityGeometryOrBudget_RefusesBeforeTool(string mutation,
        MediaWorkerStatus status, ChdDvdEncodingCause cause)
    {
        byte[] input = CreateExtractionSource();
        switch (mutation)
        {
            case "version":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(12), 4);
                break;
            case "parent":
                input[104] = 1;
                break;
            case "cdCodec":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(16), 0x63647a6c);
                break;
            case "unit":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(60), 2352);
                break;
            case "hunk":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(56), 24577);
                break;
        }
        File.WriteAllBytes(Source, input);
        var result = await Extract(MissingCandidate(), expectedBytes: mutation == "sourceBytes" ? input.Length + 1 : input.Length,
            expectedHash: mutation == "sourceHash" ? new string('a', 64) : Sha256(input),
            logicalBytes: mutation == "logicalBinding" ? 43008 : 40960,
            artifact: mutation == "sourceCap" ? input.Length - 1 : input.Length,
            logical: mutation == "logicalCap" ? 38912 : mutation == "logicalBinding" ? 43008 : 40960,
            output: mutation == "outputCap" ? 38912 : mutation == "logicalBinding" ? 43008 : 40960);
        AssertExtractionRejected(result, status, cause);
        result.ToolIdentity.ShouldBeNull();
        result.Diagnostics.ShouldBeNull();
        File.Exists(Output).ShouldBeFalse();
        File.ReadAllBytes(Source).ShouldBe(input);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2047)]
    [InlineData(40961)]
    [InlineData(10737420288)]
    public async Task ExtractAsync_UnalignedOrOversizedLogicalRequest_RejectsBeforeSource(long logicalBytes)
    {
        AssertExtractionRejected(await Extract(MissingCandidate(), logicalBytes: logicalBytes),
            MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.InvalidArguments);
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ExistingOutput_PreservesBytesBeforeTool()
    {
        CreateExtractionSource();
        byte[] other = "another caller's output"u8.ToArray();
        File.WriteAllBytes(Output, other);
        AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.OutputExists);
        File.ReadAllBytes(Output).ShouldBe(other);
    }

    [Theory]
    [InlineData(24576U, 40960)]
    [InlineData(16384U, 133120)]
    public async Task Linux_ExtractAsync_NonfactorOrPartialHunk_MeasuresEveryLogicalDigestWithBorrowedToolLease(uint hunkBytes, int logicalBytes)
    {
        byte[] input = CreateExtractionSource(hunkBytes: hunkBytes, logicalBytes: logicalBytes);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate(), logicalBytes: logicalBytes, logical: logicalBytes, output: logicalBytes),
                MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        ChdmanCandidate candidate = FixtureCandidate();
        using var lease = Lease();
        string nonce = Guid.NewGuid().ToString("N");
        lease.Bind(nonce);
        var result = await Extract(candidate, lease.Descriptor, logicalBytes: logicalBytes, logical: logicalBytes, output: logicalBytes);
        result.Status.ShouldBe(MediaWorkerStatus.Encoded);
        result.Rejection.ShouldBeNull();
        var facts = result.Extraction.ShouldNotBeNull();
        byte[] expected = Enumerable.Range(0, logicalBytes).Select(index => (byte)(index % 251)).ToArray();
        File.ReadAllBytes(Output).ShouldBe(expected);
        facts.SourceSha256.ShouldBe(Sha256(input));
        facts.SourceBytes.ShouldBe(input.Length);
        facts.SourceBytes.ShouldNotBe(facts.LogicalBytes);
        facts.LogicalSha256.ShouldBe(Sha256(expected));
        facts.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(expected)));
        facts.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(expected)));
        facts.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(expected).ToString("x8", CultureInfo.InvariantCulture));
        facts.LogicalBytes.ShouldBe(logicalBytes);
        facts.Profile.ShouldBe(ChdDvdEncodingService.ExtractionProfile);
        result.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        AssertExtractionJoined(result);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Source + ".facts"));
        var actual = report.RootElement;
        actual.GetProperty("Nonce").GetString().ShouldBe(nonce);
        actual.GetProperty("Pid").GetInt32().ShouldBe(result.Diagnostics!.ProcessId);
        AssertSealedLaunch(actual);
        actual.GetProperty("FlockDenied").GetInt32().ShouldBe(1);
        actual.GetProperty("EnvironmentCount").GetInt32().ShouldBe(0);
        actual.GetProperty("FsizeSoft").GetUInt64().ShouldBe((ulong)logicalBytes);
        actual.GetProperty("FsizeHard").GetUInt64().ShouldBe((ulong)logicalBytes);
        var inherited = InheritedExpectation();
        actual.GetProperty("GroupId").GetInt32().ShouldBe(inherited.GroupId);
        actual.GetProperty("Nice").GetInt32().ShouldBe(inherited.Nice);
        foreach (var entry in new[] { ("AddressSpace", inherited.AddressSpace), ("Data", inherited.Data), ("Cpu", inherited.Cpu), ("Core", inherited.Core) })
        {
            string[] values = entry.Item2.Split(' ');
            actual.GetProperty(entry.Item1 + "Soft").GetUInt64().ShouldBe(LimitNumber(values[0]));
            actual.GetProperty(entry.Item1 + "Hard").GetUInt64().ShouldBe(LimitNumber(values[1]));
        }
    }

    [Theory]
    [InlineData(1, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.ToolExitFailure)]
    [InlineData(2, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.OutputLengthMismatch)]
    [InlineData(3, MediaWorkerStatus.Malformed, ChdDvdEncodingCause.OutputLengthMismatch)]
    [InlineData(6, MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.DiagnosticLimitExceeded)]
    [InlineData(7, MediaWorkerStatus.LimitExceeded, ChdDvdEncodingCause.DiagnosticLimitExceeded)]
    [InlineData(10, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged)]
    [InlineData(11, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged)]
    [InlineData(15, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged)]
    [InlineData(12, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.UnsafePath)]
    [InlineData(13, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.UnsafePath)]
    public async Task Linux_ExtractAsync_ForeignProcessFault_RejectsWithoutLogicalEvidenceAndJoins(int mode,
        MediaWorkerStatus status, ChdDvdEncodingCause cause)
    {
        CreateExtractionSource(mode);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        using var lease = Lease();
        ChdmanCandidate candidate = FixtureCandidate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Extract(candidate, lease.Descriptor, ct: deadline.Token);
        AssertExtractionRejected(result, status, cause);
        AssertExtractionJoined(result);
    }

    [Fact]
    public async Task Linux_ExtractAsync_HeldInodeChangedBehindUntouchedReplacement_RejectsWithoutLogicalEvidence()
    {
        byte[] original = CreateExtractionSource(16);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        File.WriteAllBytes(Source + ".replacement", original);
        using var held = File.OpenHandle(Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lease = Lease();

        var result = await Extract(FixtureCandidate(), lease.Descriptor);

        AssertHeldInodeMutation(held, original);
        AssertExtractionRejected(result, MediaWorkerStatus.IoFailure, ChdDvdEncodingCause.SourceChanged);
        AssertExtractionJoined(result);
        AssertExactSuccessfulTool(result.Diagnostics.ShouldNotBeNull());
    }

    private void AssertHeldInodeMutation(Microsoft.Win32.SafeHandles.SafeFileHandle held, byte[] original)
    {
        // Name and held descriptor have equal lengths, but only the held descriptor has changed bytes.
        File.ReadAllBytes(Source).ShouldBe(original);
        Sha256(File.ReadAllBytes(Source)).ShouldBe(Sha256(original));
        File.Exists(Source + ".replacement").ShouldBeFalse();
        RandomAccess.GetLength(held).ShouldBe(original.Length);
        byte[] observed = new byte[original.Length];
        int offset = 0;
        while (offset < observed.Length)
        {
            int count = RandomAccess.Read(held, observed.AsSpan(offset), offset);
            count.ShouldBeGreaterThan(0);
            offset += count;
        }
        byte[] expected = (byte[])original.Clone();
        expected[256] ^= 1;
        observed.ShouldBe(expected);
        Sha256(observed).ShouldNotBe(Sha256(original));
    }

    private void AssertExactSuccessfulTool(ChdDvdToolDiagnostics diagnostics)
    {
        diagnostics.ExitCode.ShouldBe(0);
        int recordedPid = int.Parse(File.ReadAllText(Source + ".pid"), CultureInfo.InvariantCulture);
        diagnostics.ProcessId.ShouldBe(recordedPid);
        Directory.Exists($"/proc/{recordedPid}").ShouldBeFalse("the fixture has no descendants; its exact direct process and pipes must be joined");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Linux_ExtractAsync_OversizedWrite_HitsExactLogicalBoundDuringWrite(int mode)
    {
        CreateExtractionSource(mode);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        using var lease = Lease();
        var result = await Extract(FixtureCandidate(), lease.Descriptor);
        result.Extraction.ShouldBeNull();
        result.Diagnostics.ShouldNotBeNull().ExitCode.ShouldBe(153);
        new FileInfo(Output).Length.ShouldBe(40960);
        AssertExtractionJoined(result);
    }

    [Fact]
    public async Task Linux_ExtractAsync_PairedNativeWriterCaps_IsolatesDuringWriteLogicalBound()
    {
        CreateExtractionSource(8);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        ChdmanCandidate candidate = FixtureCandidate();
        using var lease = Lease();
        foreach (long cap in new[] { 40960L, 81920L })
        {
            string output = Path.Combine(_directory, "native-extract-cap-" + cap.ToString(CultureInfo.InvariantCulture) + ".iso");
            var start = new ProcessStartInfo("/usr/bin/prlimit") { UseShellExecute = false };
            start.Environment.Clear();
            foreach (string argument in new[] { "--fsize=" + cap.ToString(CultureInfo.InvariantCulture) + ":" + cap.ToString(CultureInfo.InvariantCulture),
                "--", candidate.BinaryPath, "extractdvd", "-i", Source, "-o", output })
                start.ArgumentList.Add(argument);
            using PosixMediaChild child = PosixMediaChild.StartTool(start, lease.Descriptor, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<byte[]> stdout = ReadBoundedAsync(child.Output, deadline.Token), stderr = ReadBoundedAsync(child.Error, deadline.Token);
            try
            {
                child.Input.Dispose();
                await child.WaitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr);
                child.ExitCode.ShouldBe(cap == 40960 ? 153 : 0);
                new FileInfo(output).Length.ShouldBe(cap == 40960 ? 40960 : 40961);
                using var facts = JsonDocument.Parse(File.ReadAllBytes(Source + ".facts"));
                facts.RootElement.GetProperty("Pid").GetInt32().ShouldBe(child.Id);
                facts.RootElement.GetProperty("FsizeSoft").GetUInt64().ShouldBe((ulong)cap);
                facts.RootElement.GetProperty("FsizeHard").GetUInt64().ShouldBe((ulong)cap);
            }
            finally { await child.TerminateAndWaitAsync(); await Task.WhenAll(stdout, stderr); }
            Directory.Exists($"/proc/{child.Id}").ShouldBeFalse();
        }
        // The synthetic tool requests 40961 bytes in both runs. This direct control qualifies no CHD decoding.
    }

    [Fact]
    public async Task Linux_ExtractAsync_CanceledTool_PreservesTokenAndLeaseThroughDirectJoin()
    {
        CreateExtractionSource(9);
        if (!OperatingSystem.IsLinux())
        {
            AssertExtractionRejected(await Extract(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
            return;
        }
        var lease = Lease();
        using var canceled = new CancellationTokenSource();
        Task<ChdDvdExtractionOutcome>? pending = null;
        try
        {
            pending = Extract(FixtureCandidate(), lease.Descriptor, ct: canceled.Token);
            int pid = await WaitForPidAsync();
            lease.Dispose();
            using (var denied = NativeFileLease.TryAcquire(Path.Combine(_directory, "processing.lease")))
                denied.ShouldBeNull();
            canceled.Cancel();
            try
            { await pending; throw new InvalidOperationException("Canceled extraction must throw."); }
            catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(canceled.Token); }
            Directory.Exists($"/proc/{pid}").ShouldBeFalse();
            using var available = NativeFileLease.TryAcquire(Path.Combine(_directory, "processing.lease")).ShouldNotBeNull();
        }
        finally
        {
            canceled.Cancel();
            if (pending is not null)
            { try { await pending; } catch (OperationCanceledException) { } }
            lease.Dispose();
        }
    }

    private byte[] CreateExtractionSource(int mode = 0, uint hunkBytes = 24576, int logicalBytes = 40960)
    {
        // Fixed-header-shaped foreign-process fixture, not a valid compressed map or integrity proof. The zlib codec is the
        // historical chd-dvd-canonical/v1 shape: extraction stays recipe-agnostic so v1 artifacts can re-converge.
        byte[] input = new byte[4097];
        "MComprHD"u8.CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(8), 124);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(12), 5);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(16), 0x7a6c6962);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(32), (ulong)logicalBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(56), hunkBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(60), 2048);
        Encoding.ASCII.GetBytes($"ROMD-DVD-FIXTURE:{mode}\n").CopyTo(input, 124);
        File.WriteAllBytes(Source, input);
        return input;
    }
    private Task<ChdDvdExtractionOutcome> Extract(ChdmanCandidate candidate, int descriptor = -1,
        long expectedBytes = 4097, string? expectedHash = null, long logicalBytes = 40960,
        long artifact = 4097, long logical = 40960, long output = 40960, CancellationToken ct = default) =>
        ChdDvdEncodingService.ExtractAsync(Source, Output, candidate, expectedBytes,
            expectedHash ?? (File.Exists(Source) ? Sha256(File.ReadAllBytes(Source)) : new string('a', 64)),
            logicalBytes, artifact, logical, output, "unix-nice", ToolPriority(), descriptor, ct);
    private static void AssertExtractionRejected(ChdDvdExtractionOutcome result, MediaWorkerStatus status, ChdDvdEncodingCause cause)
    {
        string observed = Describe(result.Rejection, result.Diagnostics);
        result.Status.ShouldBe(status, observed);
        result.Extraction.ShouldBeNull();
        result.Rejection.ShouldNotBeNull().Cause.ShouldBe(cause, observed);
    }
    private static void AssertExtractionJoined(ChdDvdExtractionOutcome result) =>
        Directory.Exists($"/proc/{result.Diagnostics.ShouldNotBeNull().ProcessId}").ShouldBeFalse("direct wait and pipe joins only; no tree-stop or decoder qualification");

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[4097];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                break;
            length += read;
        }
        length.ShouldBeLessThanOrEqualTo(4096, "fixed native mode8 facts must fit this test-only bound");
        return buffer.AsSpan(0, length).ToArray();
    }

    private async Task<bool> AssertUnsupportedPlatformAsync()
    {
        if (OperatingSystem.IsLinux())
            return false;
        CreateSource();
        AssertRejected(await Encode(MissingCandidate()), MediaWorkerStatus.Unsupported, ChdDvdEncodingCause.LinuxRequired);
        return true;
    }

    private Task<ChdDvdEncodingOutcome> Encode(ChdmanCandidate candidate, int descriptor = -1,
        long expectedBytes = SourceBytes, string? expectedHash = null, long artifact = SourceBytes,
        long logical = SourceBytes, long output = OutputCap, CancellationToken ct = default) =>
        ChdDvdEncodingService.EncodeAsync(Source, Output, candidate, expectedBytes,
            expectedHash ?? (File.Exists(Source) ? Sha256(File.ReadAllBytes(Source)) : new string('a', 64)),
            artifact, logical, output, "unix-nice", ToolPriority(), descriptor, ct);

    private void CreateSource(int mode = 0, int bytes = SourceBytes)
    {
        byte[] source = new byte[bytes];
        Encoding.ASCII.GetBytes($"ROMD-DVD-FIXTURE:{mode}\n").CopyTo(source, 0);
        File.WriteAllBytes(Source, source);
    }
    private NativeFileLease Lease()
    {
        RequireLinux();
        var lease = NativeFileLease.TryAcquire(Path.Combine(_directory, "processing.lease")).ShouldNotBeNull();
        lease.Bind(Guid.NewGuid().ToString("N"));
        return lease;
    }
    private ChdmanCandidate MissingCandidate() => new(Path.Combine(_directory, "missing-tool"),
        new(NativeRuntimeIdentifier.Current!, ChdmanPin.Tag, ChdmanPin.SourceSha256, 1, 4, new string('1', 64)));
    // The synthetic foreign-process payload (not chdman), compiled here and recorded under ROMD's MAME pin.
    private ChdmanCandidate FixtureCandidate()
    {
        RequireLinux();
        var candidate = ChdmanCandidate.FromBuildRecord(NativeTestBuilds.CompileChdDvdToolFixture(
            Path.Combine(_directory, "tool-fixture"), ChdmanPin.Tag, ChdmanPin.SourceSha256));
        candidate.Verify();
        return candidate;
    }
    private static void RequireLinux() => OperatingSystem.IsLinux().ShouldBeTrue("root must explicitly select this Linux route with its frozen fixture; no skip/fallback");
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ulong LimitNumber(string value) => value == "unlimited" ? ulong.MaxValue : ulong.Parse(value, CultureInfo.InvariantCulture);
    // Like the media worker, the in-process Linux encoder requires a host already lowered to nice 10-19.
    private static int ToolPriority()
    {
        if (!OperatingSystem.IsLinux())
            return 10;
        int nice = InheritedExpectation().Nice;
        nice.ShouldBeInRange(10, 19, "Linux CHD encoding tests run the encoder in-process; run the test host under `nice -n 10` like the media worker.");
        return nice;
    }
    /// <summary>The executed image is the sealed chdman descriptor; both inherited images keep every seal.</summary>
    private static void AssertSealedLaunch(JsonElement facts)
    {
        facts.GetProperty("ExecutesToolImage").GetInt32().ShouldBe(1);
        facts.GetProperty("ToolImageSeals").GetInt32().ShouldBe(SealedExecutable.ImmutableSeals);
        facts.GetProperty("WrapperImageSeals").GetInt32().ShouldBe(SealedExecutable.ImmutableSeals);
        facts.GetProperty("ExtraDescriptor").GetInt32().ShouldBe(0);
    }
    private sealed record InheritedProcess(int GroupId, int Nice, string AddressSpace, string Data, string Cpu, string Core);
    private static InheritedProcess InheritedExpectation()
    {
        string[] stat = File.ReadAllText("/proc/self/stat").Split(") ", 2, StringSplitOptions.None)[1].Split(' ');
        return new(int.Parse(stat[2], CultureInfo.InvariantCulture), int.Parse(stat[16], CultureInfo.InvariantCulture),
            Limits("Max address space"), Limits("Max data size"), Limits("Max cpu time"), Limits("Max core file size"));
        static string Limits(string name)
        {
            string line = File.ReadLines("/proc/self/limits").Single(value => value.StartsWith(name + " ", StringComparison.Ordinal));
            string[] values = line[name.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return values[0] + " " + values[1];
        }
    }
    private static void AssertRejected(ChdDvdEncodingOutcome result, MediaWorkerStatus status, ChdDvdEncodingCause cause)
    {
        string observed = Describe(result.Rejection, result.Diagnostics);
        result.Status.ShouldBe(status, observed);
        result.Artifact.ShouldBeNull();
        result.Rejection.ShouldNotBeNull().Cause.ShouldBe(cause, observed);
    }
    private static string Describe(ChdDvdEncodingRejection? rejection, ChdDvdToolDiagnostics? diagnostics) =>
        $"rejection {rejection?.Stage}/{rejection?.Cause} at {rejection?.Path}; tool exit {diagnostics?.ExitCode}, joined CPU {diagnostics?.CpuMicroseconds} us, " +
        $"stderr tail '{(diagnostics is null ? "" : System.Text.Encoding.UTF8.GetString(diagnostics.StandardErrorTail))}'";
    private static void AssertJoined(ChdDvdEncodingOutcome result)
    {
        int pid = result.Diagnostics.ShouldNotBeNull().ProcessId;
        Directory.Exists($"/proc/{pid}").ShouldBeFalse("a returned direct-tool result requires exact wait completion, not outer tree-stop proof");
    }
    private async Task<int> WaitForPidAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(Source + ".pid"))
            await Task.Delay(10, deadline.Token);
        return int.Parse(await File.ReadAllTextAsync(Source + ".pid", deadline.Token), CultureInfo.InvariantCulture);
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
