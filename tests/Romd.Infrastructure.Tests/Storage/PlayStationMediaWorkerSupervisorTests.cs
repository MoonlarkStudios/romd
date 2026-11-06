using Romd.Infrastructure.Storage;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class PlayStationMediaWorkerSupervisorTests
{
    [Theory]
    [InlineData("identified", Ps2DvdIdentificationStatus.Identified)]
    [InlineData("malformed", Ps2DvdIdentificationStatus.Malformed)]
    [InlineData("missing", Ps2DvdIdentificationStatus.NotIdentified)]
    public async Task InspectIsoAsync_RealChild_CarriesBootObservationAndReleasesInput(string mutation, Ps2DvdIdentificationStatus status)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        if (mutation == "malformed")
            SyntheticPs2Iso.SetConfiguration(bytes,
            "BOOT2 = cdrom0:\\SLUS_203.12;1\nBOOT2 = cdrom0:\\SLUS_203.12;1\n");
        if (mutation == "missing")
            SyntheticPs2Iso.SetConfiguration(bytes, "VER = 1.00\n");
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(bytes);
        var supervisor = fixture.Supervisor();

        var result = await supervisor.InspectIsoAsync(source);

        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.Inspection!.ArtifactSha256.ShouldBe(source.FileSha256.ToString());
        result.Reply.Inspection.LogicalSha256.ShouldBe(source.FileSha256.ToString());
        result.Reply.Inspection.LogicalBytes.ShouldBe(source.FileSize);
        result.Reply.IsoInspection!.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        var ps2 = result.Reply.Ps2Inspection.ShouldNotBeNull();
        ps2.Status.ShouldBe(status);
        if (status == Ps2DvdIdentificationStatus.Identified)
        {
            ps2.Facts!.BootSerial.ShouldBe(SyntheticPs2Iso.BootSerial);
            ps2.Facts.LogicalBytes.ShouldBe(source.FileSize);
        }
        else
            ps2.Facts.ShouldBeNull();
        result.Reply.Inspection.DiscKind.ShouldBeNull();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        await using var sibling = fixture.Source(SyntheticPs2Iso.Create(), "sibling");
        (await supervisor.InspectIsoAsync(sibling)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAsync_BootBearingIsoReplyOnRvzOperation_RejectsThenFreshIsoOperationSucceeds()
    {
        using var fixture = new MediaWorkerTestFixture();
        await using var source = fixture.Source(SyntheticPs2Iso.Create(), "ps2-reply-for-rvz");
        var supervisor = fixture.Supervisor();

        var rejected = await supervisor.InspectAsync(source);
        var restored = await supervisor.InspectIsoAsync(source);

        rejected.Failure.ShouldBe(MediaWorkerFailure.Protocol);
        rejected.Reply.ShouldBeNull();
        rejected.ProcessTreeStopped.ShouldBeTrue();
        restored.IsSuccess.ShouldBeTrue(restored.Failure?.ToString());
        restored.Reply!.Ps2Inspection!.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        restored.Execution!.ProcessId.ShouldNotBe(rejected.Execution!.ProcessId);
        MediaWorkerTestFixture.IsRunning(rejected.Execution.ProcessId).ShouldBeFalse();
        MediaWorkerTestFixture.IsRunning(restored.Execution.ProcessId).ShouldBeFalse();
    }

    // These are admitted child wire controls; the synthetic candidate is never loaded.
    [Theory]
    [InlineData("chd-ps2-inspected", Ps2DvdIdentificationStatus.Identified)]
    [InlineData("chd-ps2-malformed", Ps2DvdIdentificationStatus.Malformed)]
    public async Task InspectChdAsync_InjectedChildObservation_CarriesClosedFactsWithoutInventingDvdEligibility(string mode, Ps2DvdIdentificationStatus status)
    {
        using var fixture = new MediaWorkerTestFixture("chd-ready");
        var supervisor = new MediaWorkerSupervisor(fixture.Options, fixture.ChdCandidate());
        (await supervisor.HandshakeChdAsync()).IsSuccess.ShouldBeTrue();
        await using var source = fixture.Source(new byte[131072], mode);

        var result = await supervisor.InspectChdAsync(source);

        result.IsSuccess.ShouldBeTrue(result.Failure?.ToString());
        result.ProcessTreeStopped.ShouldBeTrue();
        result.Reply!.ChdInspection!.DvdMetadata.ShouldNotBeNull();
        result.Reply.Ps2Inspection!.Status.ShouldBe(status);
        result.Reply.Inspection!.DiscKind.ShouldBeNull();
        if (status == Ps2DvdIdentificationStatus.Identified)
            result.Reply.Ps2Inspection.Facts!.LogicalBytes.ShouldBe(result.Reply.Inspection.LogicalBytes);
        else
            result.Reply.Ps2Inspection.Facts.ShouldBeNull();
        MediaWorkerTestFixture.IsRunning(result.Execution!.ProcessId).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
    }
}
