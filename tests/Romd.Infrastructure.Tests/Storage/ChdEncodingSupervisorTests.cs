using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Native-free admitted-child wire and custody controls; no encoder or logical-integrity qualification.</summary>
public sealed class ChdEncodingSupervisorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-supervisor-").FullName;
    private LocalTempFileFactory Files()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(8L << 20);
        return new(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = 16L << 20, MaximumFiles = 4, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
    }
    private static async Task<MediaWorkerSupervisor> ReadyAsync(MediaWorkerTestFixture fixture)
    {
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        return supervisor;
    }
    private static void Stopped(MediaWorkerResult result, ITempFile source, IWritableTempFile output)
    {
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Execution.ShouldNotBeNull().ExitCode.ShouldBe(0);
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
    }
    private async Task HealthySiblingAsync(MediaWorkerTestFixture fixture, MediaWorkerSupervisor supervisor, ChdmanCandidate candidate)
    {
        await using var sibling = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var healthy = await supervisor.EncodeChdAsync(sibling, output, candidate);
        healthy.IsSuccess.ShouldBeTrue(healthy.Failure?.ToString());
        healthy.Reply!.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        Stopped(healthy, sibling, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStartForPlatform_ChdEncoding_UsesExactArgumentsAndIsolatedToolEnvironment(bool linux)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768]);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var start = fixture.Supervisor().CreateStartForPlatform("encode-chd", source, output, source.FileSize,
            linux, toolCandidate: candidate);
        var arguments = new List<string>();
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        if (linux)
            arguments.AddRange(["-n", "10", "/usr/bin/prlimit", "--as=" + Number(fixture.Options.LinuxAddressSpaceBytes),
            "--data=" + Number(fixture.Options.LinuxDataBytes), "--cpu=" + Number(fixture.Options.ChdEncodeCpuTimeoutSeconds),
            "--core=0", "--", fixture.Options.DotnetPath]);
        arguments.AddRange([fixture.Options.WorkerPath, "encode-chd", source.Path, output.Path,
            Number(fixture.Options.MaxArtifactBytes), Number(fixture.Options.MaxLogicalBytes), Number(source.FileSize),
            Number(output.MaximumBytes), source.FileSha256.ToString()]);
        start.FileName.ShouldBe(linux ? "/usr/bin/nice" : fixture.Options.DotnetPath);
        start.ArgumentList.ToArray().ShouldBe(arguments.ToArray());
        start.UseShellExecute.ShouldBeFalse();
        start.RedirectStandardInput.ShouldBeTrue();
        start.RedirectStandardOutput.ShouldBeTrue();
        start.RedirectStandardError.ShouldBeTrue();
        start.Environment.Keys.Order(StringComparer.Ordinal).ShouldBe(new[]
        {
            "DOTNET_EnableDiagnostics", "DOTNET_GCHeapHardLimit", "DOTNET_PROCESSOR_COUNT", "DOTNET_gcServer",
            "ROMD_CHDMAN_DIRECTORY", "ROMD_CHDMAN_IDENTITY"
        });
        start.Environment["ROMD_CHDMAN_DIRECTORY"].ShouldBe(candidate.BuildDirectory);
        start.Environment["ROMD_CHDMAN_IDENTITY"].ShouldBe(MediaWorkerProtocol.WriteToolIdentity(candidate.ExpectedIdentity).GetRawText());
        start.Environment["DOTNET_GCHeapHardLimit"].ShouldBe(fixture.Options.MemoryBytes.ToString("x", CultureInfo.InvariantCulture));
        start.Environment["DOTNET_gcServer"].ShouldBe("0");
        start.Environment["DOTNET_PROCESSOR_COUNT"].ShouldBe("2");
        start.Environment["DOTNET_EnableDiagnostics"].ShouldBe("0");
        start.ArgumentList.ShouldNotContain(candidate.BinaryPath);
        start.ArgumentList.ShouldNotContain(candidate.ManifestPath);
    }

    [Fact]
    public async Task EncodeChdAsync_StoppedRawFacts_PreservesCandidateIdentityAndToolProvenance()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;

        var result = await supervisor.EncodeChdAsync(source, output, candidate);

        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        var facts = result.Reply!.ChdEncoding.ShouldNotBeNull();
        facts.RecipeVersion.ShouldBe("chd-dvd-canonical/v2");
        facts.SourceSha256.ShouldBe(source.FileSha256.ToString());
        facts.SourceBytes.ShouldBe(source.FileSize);
        facts.LogicalBytes.ShouldBe(source.FileSize);
        facts.Header.LogicalBytes.ShouldBe((ulong)source.FileSize);
        facts.EncodingProfile.ShouldBe(ChdDvdCanonicalRecipe.EncoderProfile);
        result.Reply.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        result.Reply.ToolIdentity!.MameTag.ShouldBe(ChdmanPin.Tag);
        result.Reply.ToolExecution.ShouldNotBeNull().ProcessId.ShouldNotBe(result.Execution!.ProcessId);
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.ChdInspection.ShouldBeNull();
        result.Reply.NativeIdentity.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
        Stopped(result, source, output);
        var sealedFile = await output.SealAsync(new(Sha256.Parse(facts.ArtifactSha256), Md5.Parse(facts.ArtifactMd5),
            Sha1.Parse(facts.ArtifactSha1), facts.ArtifactBytes));
        sealedFile.IsError.ShouldBeFalse();
        await sealedFile.Value.DisposeAsync();
    }

    [Fact]
    public async Task EncodeChdAsync_LegacyExperimentalRecipeReply_RefusesThenSameOperationRecovers()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-legacy-recipe");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;

        var rejected = await supervisor.EncodeChdAsync(source, output, candidate);

        rejected.IsSuccess.ShouldBeFalse();
        rejected.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        rejected.Reply.ShouldBeNull();
        Stopped(rejected, source, output);
        await using var restored = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var restoredOutput = (await Files().ReserveOutputAsync(4096)).Value;
        var accepted = await supervisor.EncodeChdAsync(restored, restoredOutput, candidate);
        accepted.IsSuccess.ShouldBeTrue(accepted.Failure?.ToString());
        accepted.Reply!.ChdEncoding!.RecipeVersion.ShouldBe(ChdDvdCanonicalRecipe.Id);
        accepted.Reply.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        Stopped(accepted, restored, restoredOutput);
    }

    [Fact]
    public async Task Worker_RemovedCanonicalEncodeOperation_RejectsBeforeFileWork()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(new byte[32768]);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        using var lifetime = new MediaWorkerProcessLifetime();
        var start = fixture.Supervisor().CreateStart("encode-chd", source, output, source.FileSize, toolCandidate: fixture.ToolCandidate());
        start.ArgumentList[start.ArgumentList.IndexOf("encode-chd")] = "encode-chd-canonical";
        if (lifetime.JobName is { } job)
        { start.ArgumentList.Add("--job"); start.ArgumentList.Add(job); }
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        process.Start().ShouldBeTrue();
        process.StandardInput.Close();
        try
        { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            lifetime.Terminate(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await lifetime.JoinDescendantsAsync(process.Id, CancellationToken.None);
        }
        process.ExitCode.ShouldBe(64);
        process.StandardOutput.BaseStream.ReadByte().ShouldBe(-1);
        process.StandardError.BaseStream.ReadByte().ShouldBe(-1);
        File.Exists(output.Path).ShouldBeFalse();
    }

    [Theory]
    [InlineData("handshake")]
    [InlineData("inspect-rvz")]
    [InlineData("inspect-iso")]
    [InlineData("handshake-chd")]
    [InlineData("inspect-chd")]
    [InlineData("encode")]
    public async Task CreateStartForPlatform_ToolCandidate_DoesNotLeakIntoOtherOperations(string operation)
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        var tool = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768]);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var without = supervisor.CreateStartForPlatform(operation, source, output, source.FileSize, true, RomIsoDiscKind.GameCube);
        var actual = supervisor.CreateStartForPlatform(operation, source, output, source.FileSize, true, RomIsoDiscKind.GameCube, tool);
        actual.FileName.ShouldBe(without.FileName);
        actual.ArgumentList.ToArray().ShouldBe(without.ArgumentList.ToArray());
        actual.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
            .ShouldBe(without.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        actual.Environment.Keys.Any(key => key.StartsWith("ROMD_CHDMAN_", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeChdAsync_RawFacts_CanSealOnlyAfterCustodyEndsWithoutLogicalEvidence()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        var facts = result.Reply!.ChdEncoding.ShouldNotBeNull();
        facts.SourceSha256.ShouldBe(source.FileSha256.ToString());
        facts.SourceBytes.ShouldBe(source.FileSize);
        facts.LogicalBytes.ShouldBe(source.FileSize);
        facts.Header.LogicalBytes.ShouldBe((ulong)source.FileSize);
        facts.RecipeVersion.ShouldBe(ChdDvdCanonicalRecipe.Id);
        facts.EncodingProfile.ShouldBe(ChdDvdCanonicalRecipe.EncoderProfile);
        result.Reply.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        result.Reply.ToolExecution.ShouldNotBeNull().ProcessId.ShouldNotBe(result.Execution!.ProcessId);
        result.Reply.ToolExecution!.ExitCode.ShouldBe(0);
        (result.Reply.ToolExecution.StandardOutputBytes + result.Reply.ToolExecution.StandardErrorBytes).ShouldBeLessThanOrEqualTo(256L << 10);
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.ChdInspection.ShouldBeNull();
        result.Reply.Encoding.ShouldBeNull();
        result.Reply.NativeIdentity.ShouldBeNull();
        result.Reply.ChdEncodingRejection.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
        Stopped(result, source, output);
        var sealedFile = await output.SealAsync(new(Sha256.Parse(facts.ArtifactSha256), Md5.Parse(facts.ArtifactMd5),
            Sha1.Parse(facts.ArtifactSha1), facts.ArtifactBytes));
        sealedFile.IsError.ShouldBeFalse();
        await using var artifact = sealedFile.Value;
        artifact.FileSize.ShouldBe(facts.ArtifactBytes);
        artifact.FileSha256.ToString().ShouldBe(facts.ArtifactSha256);
    }

    [Theory]
    [InlineData("chd-encode-wrong-source-hash", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-source-bytes", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-over-cap", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-profile", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-recipe", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-historical-v1", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-pin", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-binary", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-wrong-recipe-version", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-missing-tool", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-missing-execution", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-worker-pid", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-tool-nonzero", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-diagnostic-overflow", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-normal-priority", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-mixed-inspection", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-rvz-reply", MediaWorkerFailure.Protocol)]
    [InlineData("chd-encode-worker-nonzero", MediaWorkerFailure.Crash)]
    public async Task EncodeChdAsync_UntrustedReply_RejectsAndRestoresSameRuntime(string mode, MediaWorkerFailure failure)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], mode);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(failure);
        result.Reply.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Theory]
    [InlineData("chd-encode-source-rejection", ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceHashMismatch)]
    [InlineData("chd-encode-candidate-rejection", ChdDvdEncodingStage.ToolVerification, ChdDvdEncodingCause.CandidateRejected)]
    public async Task EncodeChdAsync_TypedRejection_BindsPermittedPathAndAllowsFreshSibling(
        string mode, ChdDvdEncodingStage stage, ChdDvdEncodingCause cause)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], mode);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(MediaWorkerFailure.Malformed);
        result.Reply!.ChdEncoding.ShouldBeNull();
        var rejected = result.Reply.ChdEncodingRejection.ShouldNotBeNull();
        rejected.Stage.ShouldBe(stage);
        rejected.Cause.ShouldBe(cause);
        rejected.Path.ShouldBe(stage == ChdDvdEncodingStage.Source ? source.Path : candidate.BinaryPath);
        result.Reply.ToolIdentity.ShouldBeNull();
        result.Reply.ToolExecution.ShouldBeNull();
        Stopped(result, source, output);
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Fact]
    public async Task EncodeChdAsync_ToolKilledByCpuBudget_ReportsLimitAndAllowsFreshSibling()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-tool-killed");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;

        var result = await supervisor.EncodeChdAsync(source, output, candidate);

        result.Failure.ShouldBe(MediaWorkerFailure.CpuDeadline);
        result.Reply!.ChdEncoding.ShouldBeNull();
        var rejected = result.Reply.ChdEncodingRejection.ShouldNotBeNull();
        rejected.Stage.ShouldBe(ChdDvdEncodingStage.ToolExecution);
        rejected.Cause.ShouldBe(ChdDvdEncodingCause.CpuBudgetExceeded);
        rejected.Path.ShouldBe(candidate.BinaryPath);
        result.Reply.ToolIdentity.ShouldBe(candidate.ExpectedIdentity);
        result.Reply.ToolExecution.ShouldNotBeNull().ExitCode.ShouldBe(128 + 9);
        Stopped(result, source, output);
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Theory]
    [InlineData("chd-encode-external-kill", MediaWorkerFailure.Malformed)]
    [InlineData("chd-encode-false-cpu-budget", MediaWorkerFailure.Protocol)]
    public async Task EncodeChdAsync_KillWithoutBudgetEvidence_IsNotLimitExceeded(string mode, MediaWorkerFailure expected)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], mode);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(expected);
        result.ProcessTreeStopped.ShouldBeTrue();
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Fact]
    public async Task EncodeChdAsync_WallDeadline_UsesChdBudgetAndStopsBeforeOrdinaryDeadline()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        fixture.Options.ChdEncodeWallTimeoutSeconds = 1;
        fixture.Options.WallTimeoutSeconds = 10;
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "hang");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(MediaWorkerFailure.WallDeadline);
        result.ProcessTreeStopped.ShouldBeTrue();
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Theory]
    [InlineData("chd-encode-wrong-source-path")]
    [InlineData("chd-encode-wrong-output-path")]
    public async Task EncodeChdAsync_TypedRejectionForUnrelatedPath_IsProtocolFailure(string mode)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], mode);
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        Stopped(result, source, output);
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("logical")]
    [InlineData("output")]
    public async Task EncodeChdAsync_InvalidBudget_DoesNotLaunchOrAcquireCustody(string kind)
    {
        using var fixture = new MediaWorkerTestFixture();
        if (kind == "artifact")
            fixture.Options.MaxArtifactBytes = 32767;
        if (kind == "logical")
            fixture.Options.MaxLogicalBytes = 32767;
        var supervisor = fixture.Supervisor();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(kind == "output" ? 123 : 4096)).Value;
        var result = await supervisor.EncodeChdAsync(source, output, candidate);
        result.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        result.Execution.ShouldBeNull();
        result.Reply.ShouldBeNull();
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeChdAsync_OutputHoldFailure_ReleasesSourceWithoutLaunching()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = fixture.Supervisor();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        Directory.CreateDirectory(output.Path + ".quarantine");
        try
        {
            var result = await supervisor.EncodeChdAsync(source, output, candidate);
            result.Failure.ShouldBe(MediaWorkerFailure.SourceCustody);
            result.Execution.ShouldBeNull();
            File.Exists(source.Path + ".pid").ShouldBeFalse();
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        }
        finally { Directory.Delete(output.Path + ".quarantine"); }
    }

    [Fact]
    public async Task EncodeChdAsync_AdmittedCancellation_JoinsWholeGroupAndReusesSameAdmission()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "descendant-hang");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = supervisor.EncodeChdAsync(source, output, candidate, cancellation.Token);
        try
        {
            int pid = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
            int descendant = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".descendant.pid", deadline.Token);
            File.Exists(source.Path + ".quarantine").ShouldBeTrue();
            File.Exists(output.Path + ".quarantine").ShouldBeTrue();
            await Should.ThrowAsync<ResourceCapacityException>(() => supervisor.HandshakeAsync());
            await cancellation.CancelAsync();
            MediaWorkerStoppedCancellationException? error = null;
            try
            { await pending; }
            catch (MediaWorkerStoppedCancellationException stopped) { error = stopped; }
            error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
            MediaWorkerTestFixture.IsRunning(descendant).ShouldBeFalse();
            File.Exists(source.Path + ".quarantine").ShouldBeFalse();
            File.Exists(output.Path + ".quarantine").ShouldBeFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            { await pending; }
            catch (OperationCanceledException) { }
        }
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        await HealthySiblingAsync(fixture, supervisor, candidate);
    }

    [Fact]
    public async Task EncodeChdAsync_PreCancelled_DoesNotLaunchOrRetainCustody()
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-valid");
        await using var output = (await Files().ReserveOutputAsync(4096)).Value;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        MediaWorkerStoppedCancellationException? error = null;
        try
        { await fixture.Supervisor().EncodeChdAsync(source, output, candidate, cancellation.Token); }
        catch (MediaWorkerStoppedCancellationException stopped) { error = stopped; }
        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        File.Exists(output.Path).ShouldBeFalse();
        File.Exists(output.Path + ".quarantine").ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_RvzMetadata_RemainsIndependent()
    {
        using var fixture = new MediaWorkerTestFixture();
        byte[] original = SyntheticNintendoIso.GameCube();
        await using var source = fixture.Source(original);
        var cap = ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, fixture.Options.MaxLogicalBytes)).MaximumOutputBytes;
        await using var output = (await Files().ReserveOutputAsync(cap)).Value;
        var result = await fixture.Supervisor().EncodeAsync(source, output, original.Length, RomIsoDiscKind.GameCube);
        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        result.Reply!.Encoding.ShouldNotBeNull();
        result.Reply.ChdEncoding.ShouldBeNull();
        result.Reply.ToolIdentity.ShouldBeNull();
        result.Reply.ChdEncodingRejection.ShouldBeNull();
        result.Reply.ToolExecution.ShouldBeNull();
        Stopped(result, source, output);
    }

    [Fact]
    public async Task InspectIsoAsync_ChdEncodingReplyForManagedOperation_IsRejected()
    {
        using var fixture = new MediaWorkerTestFixture();
        fixture.ToolCandidate();
        await using var source = fixture.Source(new byte[32768], "chd-encode-for-managed");
        var result = await fixture.Supervisor().InspectIsoAsync(source);
        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
