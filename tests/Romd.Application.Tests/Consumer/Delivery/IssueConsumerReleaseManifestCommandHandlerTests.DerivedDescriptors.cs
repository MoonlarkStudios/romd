using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Source.Rom;
using Romd.Application.Tests.Content;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed partial class IssueConsumerReleaseManifestCommandHandlerTests
{
    private static DerivedDescriptorBinding Derived() => new(71, Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ReleaseId, 83, "disc.cue", 97, 1, 101, DeliveryFacts.Identity(93), "fixture-generator/v1");

    [Fact]
    public async Task HandleAsync_DerivedCue_UsesActualIdentityAndDedicatedGrantWithoutClaimingPreservation()
    {
        var captured = Derived();
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, CreateManifest() with
        {
            IsComplete = false,
            Items = [new("disc.cue", "rom", 137, new ConsumerReleaseManifestSource.Derived(captured))]
        }));
        _derivedGrants.Issue(Arg.Any<ConsumerDerivedDescriptorGrant>()).Returns(new ConsumerIssuedContentGrant(
            "/delivery/derived-descriptor/captured", DateTimeOffset.UtcNow.AddMinutes(1)));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.IsComplete.ShouldBeFalse();
        result.Value.IsDeliveryComplete.ShouldBeTrue();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.Provenance.ShouldBe(ConsumerReleaseFileProvenance.Derived);
        item.IsAvailable.ShouldBeTrue();
        item.Sha256.ShouldBe(captured.Identity.Sha256.ToString());
        item.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)captured.Identity.Size);
        item.SizeBytes.ShouldNotBe((Romd.Contracts.Common.Models.ByteCount)137);
        item.Formats.ShouldBeEmpty();
        item.ContentGrant!.DownloadUrl.ShouldBe("/delivery/derived-descriptor/captured");
        _derivedGrants.Received(1).Issue(new(UserId, LibraryId, TitleId, captured));
        _grantIssuer.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("release")]
    [InlineData("descriptor-release")]
    public async Task HandleAsync_InconsistentAuthorization_IssuesNoCapabilities(string change)
    {
        var captured = change == "descriptor-release" ? Derived() with { ReleaseId = ReleaseId + 1 } : Derived();
        var manifest = CreateManifest() with { Items = [new("disc.cue", "rom", 137, new ConsumerReleaseManifestSource.Derived(captured))] };
        if (change == "user")
            manifest = manifest with { UserId = Guid.NewGuid() };
        if (change == "release")
            manifest = manifest with { ReleaseId = ReleaseId + 1 };
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, manifest));
        (await CreateHandler().HandleAsync(new(ReleaseId))).IsError.ShouldBeTrue();
        _derivedGrants.ReceivedCalls().ShouldBeEmpty();
        _grantIssuer.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_MissingRequirement_IsNotDeliveryCompleteAndIssuesNoCapabilities()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, CreateManifest() with
        { IsComplete = false, Items = [new("disc.cue", "rom", 137, new ConsumerReleaseManifestSource.Missing())] }));
        var result = await CreateHandler().HandleAsync(new(ReleaseId));
        result.Value.IsComplete.ShouldBeFalse();
        result.Value.IsDeliveryComplete.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeFalse();
        item.Sha256.ShouldBeNull();
        item.ContentGrant.ShouldBeNull();
        item.Formats.ShouldBeEmpty();
        _derivedGrants.ReceivedCalls().ShouldBeEmpty();
        _grantIssuer.ReceivedCalls().ShouldBeEmpty();
    }
}
