using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Native-free, admitted child wire controls. Actual decoder behavior belongs to service qualification.</summary>
public sealed class ChdMediaWorkerSupervisorTests
{
    private static async Task<MediaWorkerSupervisor> ReadyAsync(MediaWorkerTestFixture fixture)
    {
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
        return supervisor;
    }

    [Fact]
    public async Task HandshakeChdAsync_NoExplicitCandidate_FailsWithoutChangingManagedReadiness()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = fixture.Supervisor();
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();

        var result = await supervisor.HandshakeChdAsync();

        result.Failure.ShouldBe(MediaWorkerFailure.Startup);
        result.Execution.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateStart_ChdOnlyEnvironment_PreservesExistingPlatformEnvelope(bool linux)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = fixture.ChdCandidate();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, candidate);
        foreach (string operation in new[] { "handshake", "inspect-rvz", "inspect-iso", "handshake-chd", "inspect-chd", "inspect-chd-cd" })
        {
            var start = supervisor.CreateStartForPlatform(operation, null, null, 0, linux);
            bool chd = operation is "handshake-chd" or "inspect-chd" or "inspect-chd-cd";
            start.Environment.Count.ShouldBe(chd ? 6 : 4);
            start.Environment.ContainsKey("ROMD_CHD_NATIVE_DIRECTORY").ShouldBe(chd);
            start.Environment.ContainsKey("ROMD_CHD_NATIVE_IDENTITY").ShouldBe(chd);
            start.FileName.ShouldBe(linux ? "/usr/bin/prlimit" : fixture.Options.DotnetPath);
            start.ArgumentList.Last().ShouldBe(operation);
            start.ArgumentList.ShouldNotContain(candidate.BinaryPath);
            start.ArgumentList.ShouldNotContain(candidate.ManifestPath);
            if (chd)
            {
                start.Environment["ROMD_CHD_NATIVE_DIRECTORY"].ShouldBe(candidate.BuildDirectory);
                start.Environment["ROMD_CHD_NATIVE_IDENTITY"].ShouldBe(MediaWorkerProtocol.WriteNativeIdentity(candidate.ExpectedIdentity).GetRawText());
            }
        }
    }

    [Theory]
    [InlineData("chd-wrong-native")]
    [InlineData("chd-wrong-rejection-path")]
    public async Task HandshakeChdAsync_InjectedPoison_ClearsOnlyChdAndHealthyChildRestoresReadiness(string mode)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        fixture.ChdHandshakeMode(mode);

        var result = await supervisor.HandshakeChdAsync();

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        fixture.ChdHandshakeMode("chd-ready");
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("chd-header-rejection", ChdRejectionStage.Header, "TruncatedHeader")]
    [InlineData("chd-native-rejection", ChdRejectionStage.NativeVerification, "BinaryMissing")]
    public async Task InspectChdAsync_TypedFailure_RetainsCauseAndAllowsFreshHealthySibling(
        string mode, ChdRejectionStage stage, string cause)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        await using var source = fixture.Source(new byte[124], mode);

        var result = await supervisor.InspectChdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Malformed);
        result.Reply!.ChdRejection!.Stage.ShouldBe(stage);
        result.Reply.ChdRejection.Cause.ShouldBe(cause);
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.ChdInspection.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        await using var sibling = fixture.Source(new byte[2048], "chd-inspected");
        (await supervisor.InspectChdAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("chd-wrong-artifact-length")]
    [InlineData("chd-wrong-artifact-hash")]
    [InlineData("chd-wrong-source-path")]
    [InlineData("chd-rvz-reply")]
    public async Task InspectChdAsync_InjectedCrossOperationOrCustodyContradiction_Rejects(string mode)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        byte[] artifact = mode == "chd-rvz-reply" ? new SyntheticRvzBuilder(0x100000, 0x20000).Build().File : new byte[2048];
        await using var source = fixture.Source(artifact, mode);

        var result = await supervisor.InspectChdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        supervisor.IsChdAvailable.ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        await using var sibling = fixture.Source(new byte[2048], "chd-inspected");
        (await supervisor.InspectChdAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAsync_ChdReplyForManagedOperation_RejectsWithoutChangingEitherReadiness()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        await using var source = fixture.Source(new byte[2048], "chd-for-managed");

        var result = await supervisor.InspectAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeTrue();
        await using var sibling = fixture.Source(new SyntheticRvzBuilder(0x100000, 0x20000).Build().File);
        (await supervisor.InspectAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChdOperation_PreCancelledAfterHandshake_ClearsChdAndPreservesExactCancellation(bool inspect)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        await using var source = fixture.Source(new byte[2048], "chd-inspected");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            await (inspect ? supervisor.InspectChdAsync(source, cancellation.Token) : supervisor.HandshakeChdAsync(cancellation.Token));
            throw new InvalidOperationException("The already-cancelled CHD operation must throw.");
        }
        catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(cancellation.Token); }

        supervisor.IsChdAvailable.ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectChdAsync_EarlyArtifactLimit_ClearsChdWithoutLaunchingOrQuarantiningSource()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        fixture.Options.MaxArtifactBytes = 124;
        var supervisor = await ReadyAsync(fixture);
        await using var source = fixture.Source(new byte[125], "chd-inspected");

        var result = await supervisor.InspectChdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.LimitExceeded);
        result.Execution.ShouldBeNull();
        result.Reply.ShouldBeNull();
        supervisor.IsChdAvailable.ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectChdAsync_CancelAdmittedChild_JoinsAndReusesSameAdmission()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = await ReadyAsync(fixture);
        await using var source = fixture.Source(new byte[2048], "gated");
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = supervisor.InspectChdAsync(source, cancellation.Token);
        int pid = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
        await cancellation.CancelAsync();
        try
        { await pending; throw new InvalidOperationException("The admitted CHD operation must be cancelled."); }
        catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(cancellation.Token); }

        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        supervisor.IsChdAvailable.ShouldBeFalse();
        supervisor.IsAvailable.ShouldBeTrue();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        await using var sibling = fixture.Source(new byte[2048], "chd-inspected");
        (await supervisor.InspectChdAsync(sibling)).IsSuccess.ShouldBeTrue();
    }
}
