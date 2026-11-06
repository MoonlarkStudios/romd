using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed partial class IssueConsumerDiscSetManifestTests
{
    private (ConsumerDiscSetPlan Plan, DerivedDescriptorBinding Captured) DerivedDisc()
    {
        var disc = plan.Discs[0];
        var items = disc.Release.Items.ToArray();
        var identity = ((ConsumerReleaseManifestSource.Original)items[0].Source).Identity;
        var captured = new DerivedDescriptorBinding(71, Guid.NewGuid(), disc.Release.ReleaseId, 83, "game.cue", 97, 1, 101,
            identity, "fixture-generator/v1");
        items[0] = items[0] with { Source = new ConsumerReleaseManifestSource.Derived(captured) };
        var value = plan with { Discs = [disc with { Release = disc.Release with { IsComplete = false, Items = items } }, plan.Discs[1]] };
        derivedSource.IsRetainedAsync(captured, Arg.Any<CancellationToken>()).Returns(true);
        artifacts.ResolveAsync(identity.Sha256, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes[10])));
        derivedIssuer.Issue(Arg.Any<ConsumerDerivedDescriptorGrant>()).Returns(new ConsumerIssuedContentGrant(
            "/delivery/derived-descriptor/captured", DateTimeOffset.UtcNow.AddMinutes(5)));
        return (value, captured);
    }

    [Fact]
    public async Task HandleAsync_DerivedCue_PreservesActualCaptureAndProvenanceWithoutInventingRom()
    {
        var (value, captured) = DerivedDisc();
        SetPlan(value);
        var result = await Issue();
        result.IsError.ShouldBeFalse();
        var cue = result.Value.Discs[0].Files[0];
        cue.Provenance.ShouldBe(ConsumerReleaseFileProvenance.Derived);
        cue.Sha256.ShouldBe(captured.Identity.Sha256.ToString());
        cue.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)captured.Identity.Size);
        cue.ContentGrant.DownloadUrl.ShouldBe("/delivery/derived-descriptor/captured");
        result.Value.Discs.SelectMany(disc => disc.Files).Skip(1).ShouldAllBe(file => file.Provenance == ConsumerReleaseFileProvenance.Original);
        derivedIssuer.Received(1).Issue(new(user.UserId!.Value, 7, value.Discs[0].Release.TitleId, captured));
        grants.Count.ShouldBe(5);
        grants.ShouldAllBe(grant => grant.Target.FileId != captured.FileId);
        await reader.DidNotReceive().OpenAsync(Arg.Is<RomDeliveryTarget>(target => target.FileId == 10), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task HandleAsync_StaleDerivedCapture_RefusesBeforeCasAndBeforeAnyCapabilities()
    {
        var (value, captured) = DerivedDisc();
        derivedSource.IsRetainedAsync(captured, Arg.Any<CancellationToken>()).Returns(false);
        SetPlan(value);
        (await Issue()).FirstError.Code.ShouldBe("DiscSet.Unavailable");
        artifacts.ReceivedCalls().ShouldBeEmpty();
        grants.ShouldBeEmpty();
        derivedIssuer.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DerivedRevisionChangesWithSameBytes_ChangesObservationAndSnapshot()
    {
        var (value, captured) = DerivedDisc();
        SetPlan(value);
        var first = await Issue();
        first.IsError.ShouldBeFalse();
        var changed = captured with { Revision = Guid.NewGuid() };
        var disc = value.Discs[0];
        var items = disc.Release.Items.ToArray();
        items[0] = items[0] with { Source = new ConsumerReleaseManifestSource.Derived(changed) };
        var next = value with { Discs = [disc with { Release = disc.Release with { Items = items } }, value.Discs[1]] };
        derivedSource.IsRetainedAsync(changed, Arg.Any<CancellationToken>()).Returns(true);
        SetPlan(next);
        var second = await Issue();
        second.IsError.ShouldBeFalse();
        second.Value.DeliverySnapshotId.ShouldNotBe(first.Value.DeliverySnapshotId);
        second.Value.ObservationId.ShouldNotBe(first.Value.ObservationId);
        second.Value.Discs[0].Files[0].Sha256.ShouldBe(first.Value.Discs[0].Files[0].Sha256);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("cross-release")]
    public async Task HandleAsync_InvalidDerivedSource_IssuesNoCapabilities(string defect)
    {
        var (value, captured) = DerivedDisc();
        if (defect == "cross-release")
        {
            var disc = value.Discs[0];
            var items = disc.Release.Items.ToArray();
            items[0] = items[0] with { Source = new ConsumerReleaseManifestSource.Derived(captured with { ReleaseId = 2 }) };
            value = value with { Discs = [disc with { Release = disc.Release with { Items = items } }, value.Discs[1]] };
        }
        else
        {
            byte[] bad = defect switch { "corrupt" => bytes[10].Select(b => (byte)(b ^ 1)).ToArray(), "truncated" => bytes[10][..^1], _ => [.. bytes[10], 0] };
            artifacts.ResolveAsync(captured.Identity.Sha256, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bad)));
        }
        SetPlan(value);
        (await Issue()).IsError.ShouldBeTrue();
        grants.ShouldBeEmpty();
        derivedIssuer.ReceivedCalls().ShouldBeEmpty();
    }
}
