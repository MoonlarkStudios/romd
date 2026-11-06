using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdMediaWorkerSupervisorTests
{
    [Theory]
    [InlineData("cd-cdlz.chd")]
    [InlineData("cd-cdzl.chd")]
    [InlineData("cd-cdfl.chd")]
    [InlineData("cd-cdzs.chd")]
    [InlineData("cd-edge-partial.chd")]
    public async Task InspectChdCdAsync_ActualChild_ReturnsVerifiedTracksAndJoinedCustody(string fixtureName)
    {
        using var fixture = new MediaWorkerTestFixture();
        var candidate = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        var supervisor = new MediaWorkerSupervisor(fixture.Options, candidate);
        await using var source = fixture.Source(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture(fixtureName)));

        var result = await supervisor.InspectChdCdAsync(source);

        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        result.Reply.CdInspection!.ArtifactSha256.ShouldBe(source.FileSha256.ToString());
        result.Reply.CdInspection.Tracks.Count.ShouldBe(2);
        result.Reply.Inspection.ShouldBeNull();
        result.Reply.ChdInspection.ShouldBeNull();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }

    [Theory]
    [InlineData("chd-cd-wrong-length")]
    [InlineData("chd-cd-wrong-hash")]
    [InlineData("chd-cd-wrong-native")]
    [InlineData("chd-inspected")]
    public async Task InspectChdCdAsync_CustodyOrDvdContradiction_Rejects(string mode)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        (await supervisor.HandshakeAsync()).IsSuccess.ShouldBeTrue();
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        await using var source = fixture.Source(new byte[2048], mode);

        var result = await supervisor.InspectChdCdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
        supervisor.IsAvailable.ShouldBeTrue();
        supervisor.IsChdAvailable.ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectChdAsync_CdReplyCannotEnterDvdImportPath()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        await using var source = fixture.Source(new byte[2048], "chd-cd-inspected");

        var result = await supervisor.InspectChdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        result.Reply.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectChdCdAsync_OversizedOutput_RejectsAndJoinsChild()
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        await using var source = fixture.Source(new byte[2048], "stdout-overflow");

        var result = await supervisor.InspectChdCdAsync(source);

        result.Failure.ShouldBe(MediaWorkerFailure.OutputLimit);
        result.Reply.ShouldBeNull();
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Execution!.StandardOutputBytes.ShouldBe(MediaWorkerProtocol.MaximumCdOutputBytes + 1L);
        MediaWorkerTestFixture.IsRunning(result.Execution.ProcessId).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectChdCdAsync_PreCancelled_NeverStartsChild()
    {
        using var fixture = new MediaWorkerTestFixture();
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        await using var source = fixture.Source(new byte[2048]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Capture directly: Shouldly's async wrapper can replace the cancellation subtype.
        try
        {
            await supervisor.InspectChdCdAsync(source, cancellation.Token);
            throw new InvalidOperationException("The already-cancelled CD inspection must throw.");
        }
        catch (MediaWorkerStoppedCancellationException error) { error.CancellationToken.ShouldBe(cancellation.Token); }

        File.Exists(source.Path + ".pid").ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }
}
