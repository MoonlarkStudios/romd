using System.Buffers.Binary;
using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Real admitted child and synthetic tool controls; no native decoding or source integrity qualification.</summary>
public sealed class ChdDvdExtractionSupervisorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "romd-chd-extraction-" + Guid.NewGuid().ToString("N"))).FullName;
    private LocalTempFileFactory Files()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(8L << 20);
        return new(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = 16L << 20, MaximumFiles = 4, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStartForPlatform_Extraction_SeparatesCompressedSizeFromLogicalBoundAndPinsToolOnly(bool linux)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[4097]);
        await using var output = (await Files().ReserveOutputAsync(133120)).Value;
        var start = fixture.Supervisor().CreateStartForPlatform("extract-chd-dvd", source, output, 133120, linux, toolCandidate: candidate);
        var arguments = new List<string>();
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        if (linux)
            arguments.AddRange(["-n", "10", "/usr/bin/prlimit", "--as=" + Number(fixture.Options.LinuxAddressSpaceBytes),
            "--data=" + Number(fixture.Options.LinuxDataBytes), "--cpu=" + Number(fixture.Options.CpuTimeoutSeconds),
            "--core=0", "--", fixture.Options.DotnetPath]);
        arguments.AddRange([fixture.Options.WorkerPath, "extract-chd-dvd", source.Path, output.Path,
            Number(fixture.Options.MaxArtifactBytes), Number(fixture.Options.MaxLogicalBytes), "4097", "133120", source.FileSha256.ToString(), "133120"]);
        start.ArgumentList.ToArray().ShouldBe(arguments.ToArray());
        start.FileName.ShouldBe(linux ? "/usr/bin/nice" : fixture.Options.DotnetPath);
        start.Environment.Count.ShouldBe(6);
        start.Environment["ROMD_CHDMAN_DIRECTORY"].ShouldBe(candidate.BuildDirectory);
        start.Environment["ROMD_CHDMAN_IDENTITY"].ShouldBe(MediaWorkerProtocol.WriteToolIdentity(candidate.ExpectedIdentity).GetRawText());
        start.Environment.Keys.Any(key => key.StartsWith("ROMD_CHD_NATIVE_", StringComparison.Ordinal)).ShouldBeFalse();
        start.ArgumentList.ShouldNotContain(candidate.BinaryPath);
        start.ArgumentList.ShouldNotContain(candidate.ManifestPath);
        start.UseShellExecute.ShouldBeFalse();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("logical")]
    [InlineData("unaligned")]
    [InlineData("output")]
    public async Task ExtractChdDvdAsync_InvalidBound_DoesNotLaunchOrAcquireFileCustody(string field)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[field == "source" ? 123 : 4097]);
        await using var output = (await Files().ReserveOutputAsync(field == "output" ? 2048 : 40960)).Value;
        var result = await fixture.Supervisor().ExtractChdDvdAsync(source, output, candidate, field == "logical" ? 0 : field == "unaligned" ? 40961 : 40960);
        result.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        result.Execution.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        output.TryBeginChildUse().ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
    }

    [Fact]
    public async Task ExtractChdDvdAsync_AuthenticatedIsoEncoderVariant_RejectsWithoutFallbackThenLegacyEncodingStillSucceeds()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        var supervisor = fixture.Supervisor();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(32768)).Value;
        var refused = await supervisor.ExtractChdDvdAsync(source, output, candidate, 32768);
        refused.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        refused.Reply.ShouldBeNull();
        AssertStopped(refused);
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        var legacy = await supervisor.EncodeChdAsync(source, output, candidate);
        legacy.IsSuccess.ShouldBeTrue();
        legacy.Reply!.ChdExtraction.ShouldBeNull();
        legacy.Reply.ChdEncoding.ShouldNotBeNull();
        AssertStopped(legacy);
    }

    [Fact]
    public async Task ExtractChdDvdAsync_AuthenticatedExtractionVariant_EncodingRejectsThenExtractionSucceeds()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        var supervisor = fixture.Supervisor();
        await using var source = fixture.Source(new byte[32768], "chd-extract-valid");
        await using var output = (await Files().ReserveOutputAsync(32768)).Value;
        var refused = await supervisor.EncodeChdAsync(source, output, candidate);
        refused.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        refused.Reply.ShouldBeNull();
        AssertStopped(refused);
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        var extraction = await supervisor.ExtractChdDvdAsync(source, output, candidate, 32768);
        extraction.IsSuccess.ShouldBeTrue();
        extraction.Reply!.ChdEncoding.ShouldBeNull();
        extraction.Reply.ChdExtraction.ShouldNotBeNull();
        AssertStopped(extraction);
        File.ReadAllBytes(output.Path).ShouldBe(Enumerable.Range(0, 32768).Select(index => (byte)(index % 251)).ToArray());
        output.TryBeginChildUse().ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
    }

    [Theory]
    [InlineData(24576U, 40960)]
    [InlineData(16384U, 133120)]
    public async Task ExtractChdDvdAsync_FreshChildAndSyntheticTool_ReturnsMeasuredQuartetOnlyAfterCustodyEnds(uint hunkBytes, int logicalBytes)
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = fixture.Supervisor();
        var candidate = OperatingSystem.IsLinux() ? SyntheticTool() : fixture.ToolCandidate();
        await using var source = new MediaWorkerTestFixture.Input(Path.Combine(_root, "source.chd"), Input(hunkBytes, logicalBytes));
        await using var output = (await Files().ReserveOutputAsync(logicalBytes)).Value;
        var result = await supervisor.ExtractChdDvdAsync(source, output, candidate, logicalBytes);
        AssertStopped(result);
        if (!OperatingSystem.IsLinux())
        {
            result.Failure.ShouldBe(MediaWorkerFailure.Unsupported);
            result.Reply!.ChdEncodingRejection!.Cause.ShouldBe(ChdDvdEncodingCause.LinuxRequired);
            result.Reply.ChdExtraction.ShouldBeNull();
            return;
        }
        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        var facts = result.Reply!.ChdExtraction.ShouldNotBeNull();
        byte[] expected = Enumerable.Range(0, logicalBytes).Select(index => (byte)(index % 251)).ToArray();
        facts.SourceBytes.ShouldBe(source.FileSize);
        facts.SourceSha256.ShouldBe(source.FileSha256.ToString());
        facts.LogicalSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(expected)));
        facts.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(expected)));
        facts.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(expected)));
        facts.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(expected).ToString("x8", CultureInfo.InvariantCulture));
        facts.LogicalBytes.ShouldBe(logicalBytes);
        result.Reply.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        result.Reply.ChdEncoding.ShouldBeNull();
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.ChdInspection.ShouldBeNull();
        result.Reply.NativeIdentity.ShouldBeNull();
        result.Reply.Ps2Inspection.ShouldBeNull();
        result.Reply.ToolExecution!.ProcessId.ShouldNotBe(result.Execution!.ProcessId);
        using var report = JsonDocument.Parse(File.ReadAllBytes(source.Path + ".facts"));
        report.RootElement.GetProperty("Pid").GetInt32().ShouldBe(result.Reply.ToolExecution.ProcessId);
        output.TryBeginChildUse().ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
        var sealedOutput = await output.SealAsync(new(Sha256.Parse(facts.LogicalSha256), Md5.Parse(facts.Md5), Sha1.Parse(facts.Sha1), facts.LogicalBytes));
        sealedOutput.IsError.ShouldBeFalse();
        await using var artifact = sealedOutput.Value;
        artifact.FileSize.ShouldBe(logicalBytes);
        artifact.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(expected)));
        File.ReadAllBytes(artifact.Path).ShouldBe(expected);
        // This test seals only its deterministic synthetic expectation. Production first compares the verified source quartet.
    }

    [Fact]
    public async Task ExtractChdDvdAsync_PreCanceled_PreservesCertificateAndTokenWithoutCustody()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[4097]);
        await using var output = (await Files().ReserveOutputAsync(40960)).Value;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        { await fixture.Supervisor().ExtractChdDvdAsync(source, output, candidate, 40960, canceled.Token); throw new InvalidOperationException("Canceled extraction must throw."); }
        catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(canceled.Token); }
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        output.TryBeginChildUse().ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
    }

    [Fact]
    public async Task ExtractChdDvdAsync_AdmittedCancellation_JoinsAndPreservesBorrowedReservationUntilCallerReleases()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        var supervisor = fixture.Supervisor();
        await using var source = fixture.Source(new byte[4097], "hang");
        await using var output = (await Files().ReserveOutputAsync(40960)).Value;
        using var outer = await supervisor.Runtime.AcquireAsync();
        using var active = outer.Activate();
        using var canceled = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = supervisor.ExtractChdDvdAsync(source, output, candidate, 40960, canceled.Token);
        try
        {
            int pid = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
            canceled.Cancel();
            try
            { await pending; throw new InvalidOperationException("Canceled extraction must throw."); }
            catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(canceled.Token); }
            MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
            outer.ProcessesStopped.ShouldBeTrue();
            outer.Released.ShouldBeFalse();
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            output.TryBeginChildUse().ShouldBeTrue();
            output.TryEndChildUse().ShouldBeTrue();
            outer.Dispose();
            outer.Released.ShouldBeTrue();
            using var available = await supervisor.Runtime.AcquireAsync(ct: deadline.Token);
        }
        finally { canceled.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task ExtractChdDvdAsync_UncertifiedGroupJoin_QuarantinesBothAndRetainsOuterAdmissionAfterCallerDisposal()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        var runtime = new MediaProcessingRuntime(Path.Combine(_root, "uncertain-runtime"), fixture.Options);
        var supervisor = new MediaWorkerSupervisor(fixture.Options,
            () => new MediaWorkerProcessLifetime((_, _) => Task.FromException(new IOException("injected stop observation failure"))), runtime: runtime);
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(32768)).Value;
        using var outer = await runtime.AcquireAsync();
        using var active = outer.Activate();
        var result = await supervisor.ExtractChdDvdAsync(source, output, candidate, 32768);
        result.Failure.ShouldBe(MediaWorkerFailure.Containment);
        result.ProcessTreeStopped.ShouldBeFalse();
        result.Reply.ShouldBeNull();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        output.TryBeginChildUse().ShouldBeFalse();
        outer.ProcessesStopped.ShouldBeFalse();
        outer.Dispose();
        outer.Released.ShouldBeFalse();
        try
        { using var wronglyAvailable = await runtime.AcquireAsync(); throw new InvalidOperationException("Uncertain admission must remain unavailable."); }
        catch (ResourceCapacityException) { }
        // Exact uncertainty is deliberately retained. No child remains, but the injected observer supplies no stop certificate.
    }

    private static byte[] Input(uint hunkBytes, int logicalBytes)
    {
        byte[] bytes = new byte[4097];
        "MComprHD"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 0x7a6c6962);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), (ulong)logicalBytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), 2048);
        Encoding.ASCII.GetBytes("ROMD-DVD-FIXTURE:0\n").CopyTo(bytes, 124);
        return bytes;
    }
    // The synthetic foreign-process payload (not chdman), compiled here and recorded under ROMD's MAME pin.
    private ChdmanCandidate SyntheticTool() => ChdmanCandidate.FromBuildRecord(NativeTestBuilds.CompileChdDvdToolFixture(
        Path.Combine(_root, "tool-fixture"), ChdmanPin.Tag, ChdmanPin.SourceSha256));
    private static void AssertStopped(MediaWorkerResult result)
    {
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Execution.ShouldNotBeNull().ExitCode.ShouldBe(0);
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
