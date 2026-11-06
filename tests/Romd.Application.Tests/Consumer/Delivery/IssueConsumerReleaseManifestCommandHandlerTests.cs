using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application;
using Romd.Domain.Hashing;
using Shouldly;
using Xunit;
using Romd.Application.Tests.Content;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed partial class IssueConsumerReleaseManifestCommandHandlerTests
{
    private const int LibraryId = 7;
    private const int ReleaseId = 11;
    private const int TitleId = 13;
    private const int PlatformId = 17;
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ServerInstanceId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConsumerReleaseManifestRepository _repository =
        Substitute.For<IConsumerReleaseManifestRepository>();
    private readonly IConsumerContentGrantIssuer _grantIssuer = Substitute.For<IConsumerContentGrantIssuer>();
    private readonly IConsumerDerivedDescriptorGrantIssuer _derivedGrants = Substitute.For<IConsumerDerivedDescriptorGrantIssuer>();
    private readonly IServerInstanceIdentity _serverIdentity = Substitute.For<IServerInstanceIdentity>();

    public IssueConsumerReleaseManifestCommandHandlerTests()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(UserId);
        _serverIdentity.InstanceId.Returns(ServerInstanceId);
    }

    [Fact]
    public async Task HandleAsync_Manifest_IncludesRequiredCanonicalServerInstanceId()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, CreateManifest()));

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.ServerInstanceId.ShouldBe(ServerInstanceId.ToString("D"));
        result.Value.ReleaseId.ShouldBe(IdCoder.Encode(ReleaseId));
        result.Value.TitleId.ShouldBe(IdCoder.Encode(TitleId));
    }

    [Fact]
    public async Task HandleAsync_LibraryUnavailable_ReturnsLibraryNotFound()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.LibraryUnavailable());

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.LibraryNotFound().Code);
    }

    [Fact]
    public async Task HandleAsync_ItemNotFound_ReturnsContentNotFound()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.ItemNotFound(LibraryId));

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.ContentNotFound().Code);
    }

    [Fact]
    public async Task HandleAsync_ProjectionInconsistent_ReturnsContentNotFoundWithoutGrant()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.ProjectionInconsistent());

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.ContentNotFound().Code);
        _grantIssuer.DidNotReceive().IssueDownloadGrant(Arg.Any<ConsumerContentGrant>());
    }

    [Fact]
    public async Task HandleAsync_FoundManifest_UsesOnlyOuterLiveLibraryIdForGrant()
    {
        var sha256 = Sha256.Parse(new string('a', 64));
        var target = new RomDeliveryTarget(19, 23, RomDeliveryFormat.Original, "game.sfc", sha256, 31, sha256, 31, sha256, 31, RomRepresentationEncoding.Identity);
        var manifest = CreateManifest() with
        {
            Items =
            [
                new ConsumerReleaseManifestItem("game.sfc","rom",31,new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(31),
                    [new("game.sfc",new(RomDeliveryFormat.Original,RomDeliveryState.Available,target,"identity/v1",[]))]))
            ]
        };
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, manifest));
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>())
            .Returns(new ConsumerIssuedContentGrant(
                "/delivery/content/token",
                DateTimeOffset.UtcNow.AddMinutes(1)));

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeFalse();
        _grantIssuer.Received(2).IssueDownloadGrant(new ConsumerContentGrant(UserId, LibraryId, TitleId, ReleaseId, target));
        result.Value.Items[0].ContentGrant.ShouldNotBeNull();
        result.Value.Items[0].Formats.ShouldHaveSingleItem().ContentGrant.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleAsync_PerFormatPlans_IssuesOnlyAvailableBindingsAndKeepsOriginalGrant()
    {
        var logical = Sha256.Parse(new string('a', 64));
        var artifact = Sha256.Parse(new string('b', 64));
        var identity = DeliveryFacts.Identity(100);
        var retained = DeliveryFacts.Rvz(19, 23, 51, 61, identity, artifact, 20, 1, true, null, null);
        var descriptor = new RomContentDescriptor(19, 61, identity, "disc.iso", [retained], [], []);
        var plans = RomDeliveryPlanner.Plan(descriptor, "folder/disc.iso", TestDeliveryPolicies.Production,
            MediaKindResolver.Resolve(TestMediaKinds.Production, descriptor, ["gc"]), new DeliveryAdmission(DeliveryFacts.ManagedBuild, null));
        var manifest = CreateManifest() with
        {
            Items = [new("folder/disc.iso", "rom", 100, new ConsumerReleaseManifestSource.Original(identity, plans.Select(plan => new ConsumerReleaseManifestFormat(plan.Format == RomDeliveryFormat.Rvz
                ? "folder/disc.rvz" : "folder/disc.iso", plan)).ToArray()))]
        };
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, manifest));
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>()).Returns(call =>
            new ConsumerIssuedContentGrant("/delivery/content/" + RomDeliveryFormats.ToWire(call.Arg<ConsumerContentGrant>().Target.Format),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.Sha256.ShouldBe(logical.ToString());
        item.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)100);
        item.ContentGrant!.DownloadUrl.ShouldEndWith("original");
        var original = item.Formats.Single(format => format.Format == "original");
        original.Sha256.ShouldBe(logical.ToString());
        original.Label.ShouldBe("Original");
        original.FileExtension.ShouldBeNull();
        var rvz = item.Formats.Single(format => format.Format == "rvz");
        rvz.Label.ShouldBe("RVZ");
        rvz.FileExtension.ShouldBe(".rvz");
        rvz.RelativePath.ShouldBe("folder/disc.rvz");
        rvz.Sha256.ShouldBe(artifact.ToString());
        rvz.ProfileVersion.ShouldBe("rvz-import/v1");
        rvz.RuntimeApplicability.ShouldBe(["dolphin"]);
        _grantIssuer.Received(1).IssueDownloadGrant(Arg.Is<ConsumerContentGrant>(grant =>
            grant.Target.Format == RomDeliveryFormat.Rvz && grant.Target == plans[1].Target && grant.LibraryId == LibraryId));
        _grantIssuer.Received(2).IssueDownloadGrant(Arg.Is<ConsumerContentGrant>(grant =>
            grant.Target.Format == RomDeliveryFormat.Original && grant.Target == plans[0].Target));
    }

    [Fact]
    public async Task HandleAsync_FormatDisplay_ComesFromTheDeliveryCatalog()
    {
        var formats = new RomDeliveryFormatCatalog([new(RomDeliveryFormat.Original, "Plain", null),
            new(RomDeliveryFormat.Rvz, "Dolphin RVZ", ".rvz"), new(RomDeliveryFormat.Chd, "Compressed", ".chd")]);
        var format = new ConsumerReleaseManifestFormat("disc.rvz", new(RomDeliveryFormat.Rvz, RomDeliveryState.Unsupported, null,
            "unknown", []));
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId,
            CreateManifest() with { Items = [new("disc.iso", "rom", 10, new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(10), [format]))] }));

        var result = await CreateHandler(new RomDeliveryPolicy(formats, TestMediaKinds.Production)).HandleAsync(new(ReleaseId));

        var rvz = result.Value.Items.ShouldHaveSingleItem().Formats.ShouldHaveSingleItem();
        rvz.Label.ShouldBe("Dolphin RVZ");
        rvz.FileExtension.ShouldBe(".rvz");
    }

    [Theory]
    [InlineData(RomDeliveryState.Unsupported)]
    [InlineData(RomDeliveryState.Preparing)]
    public async Task HandleAsync_UnavailableFormat_NeverIssuesGrant(RomDeliveryState state)
    {
        var format = new ConsumerReleaseManifestFormat("disc.rvz", new(RomDeliveryFormat.Rvz, state, null, "unknown", []));
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId,
            CreateManifest() with { Items = [new("disc.iso", "rom", 10, new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(10), [format]))] }));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.ContentGrant.ShouldBeNull();
        item.Formats.ShouldHaveSingleItem().ContentGrant.ShouldBeNull();
        item.Formats[0].State.ShouldBe(RomDeliveryFormats.ToWire(state));
        _grantIssuer.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ChdFormat_UsesArtifactGrantAndRuntimeWhileOriginalRemainsUnavailable()
    {
        var target = CreateChdTarget();
        var logicalRuntime = new ConsumerReleaseRuntime("single_rom", new("file", "folder/disc.iso"), "direct_files", 8192);
        var chdRuntime = new ConsumerReleaseRuntime("single_rom", new("file", "folder/disc.chd"), "direct_files", 4096);
        var format = new ConsumerReleaseManifestFormat("folder/disc.chd",
            new(RomDeliveryFormat.Chd, RomDeliveryState.Available, target, "chd-observed/v1", ["synthetic-qualified-runtime"]), chdRuntime);
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId,
            CreateManifest() with
            {
                PlatformShortName = "ps2",
                Runtime = logicalRuntime,
                Items = [new("folder/disc.iso", "disk", 8192, new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(8192), [format]))]
            }));
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>()).Returns(new ConsumerIssuedContentGrant(
            "/delivery/content/chd-token", DateTimeOffset.UtcNow.AddMinutes(1)));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.Runtime.Launch!.RelativePath.ShouldBe("folder/disc.iso");
        result.Value.Runtime.MinimumInstallBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)8192);
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.RelativePath.ShouldBe("folder/disc.iso");
        item.Sha256.ShouldBeNull();
        item.IsAvailable.ShouldBeFalse();
        result.Value.IsDeliveryComplete.ShouldBeFalse();
        item.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)8192);
        item.ContentGrant.ShouldBeNull();
        var chd = item.Formats.ShouldHaveSingleItem();
        chd.Format.ShouldBe("chd");
        chd.Label.ShouldBe("CHD");
        chd.FileExtension.ShouldBe(".chd");
        chd.Sha256.ShouldBe(target.ArtifactSha256.ToString());
        chd.Md5.ShouldBe(target.Md5!.Value.ToString());
        chd.Sha1.ShouldBe(target.Sha1!.Value.ToString());
        chd.SizeBytes.ShouldBe((Romd.Contracts.Common.Models.ByteCount)4096);
        chd.Runtime.ShouldNotBeNull();
        chd.Runtime.Launch.ShouldNotBeNull();
        chd.Runtime.Launch.RelativePath.ShouldBe(chd.RelativePath);
        chd.Runtime.MinimumInstallBytes.ShouldBe(chd.SizeBytes!.Value);
        chd.RuntimeApplicability.ShouldBe(["synthetic-qualified-runtime"]);
        chd.ContentGrant!.DownloadUrl.ShouldBe("/delivery/content/chd-token");
        _grantIssuer.Received(1).IssueDownloadGrant(new(UserId, LibraryId, TitleId, ReleaseId, target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ExplicitOriginalUnavailableOrEmpty_NeverFallsBackToUnboundGrant(bool empty)
    {
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>()).Returns(new ConsumerIssuedContentGrant(
            "/delivery/content/control-observable", DateTimeOffset.UtcNow.AddMinutes(1)));
        IReadOnlyList<ConsumerReleaseManifestFormat> formats = empty ? [] :
            [new("disc.iso", new(RomDeliveryFormat.Original, RomDeliveryState.Unsupported, null, "unknown", []))];
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId,
            CreateManifest() with
            {
                Items = [new("disc.iso", "disk", 8192, new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(8192), formats))]
            }));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldHaveSingleItem().ContentGrant.ShouldBeNull();
        _grantIssuer.DidNotReceive().IssueDownloadGrant(Arg.Any<ConsumerContentGrant>());
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-binding")]
    [InlineData("missing-md5")]
    [InlineData("missing-sha1")]
    [InlineData("logical-checksum")]
    [InlineData("different-format")]
    public async Task HandleAsync_ChdAvailableWithInvalidBinding_NormalizesUnavailableAndOmitsGrantAndRuntime(string defect)
    {
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>()).Returns(new ConsumerIssuedContentGrant(
            "/delivery/content/control-observable", DateTimeOffset.UtcNow.AddMinutes(1)));
        var complete = CreateChdTarget();
        RomDeliveryTarget? target = defect switch
        {
            "missing-target" => null,
            "missing-binding" => complete with { ReadBinding = null },
            "missing-md5" => complete with { Md5 = null },
            "missing-sha1" => complete with { Sha1 = null },
            "logical-checksum" => complete with { Sha256 = complete.ContentSha256, SizeBytes = complete.ContentSizeBytes },
            "different-format" => complete with
            {
                Format = RomDeliveryFormat.Original,
                Sha256 = complete.ContentSha256,
                SizeBytes = complete.ContentSizeBytes,
                Filename = "disc.iso"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var format = new ConsumerReleaseManifestFormat("disc.chd",
            new(RomDeliveryFormat.Chd, RomDeliveryState.Available, target, "chd-observed/v1", ["synthetic-qualified-runtime"]),
            new("single_rom", new("file", "disc.chd"), "direct_files", 4096));
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId,
            CreateManifest() with { Items = [new("disc.iso", "disk", 8192, new ConsumerReleaseManifestSource.Original(DeliveryFacts.Identity(8192), [format]))] }));

        var result = await CreateHandler().HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.ContentGrant.ShouldBeNull();
        var chd = item.Formats.ShouldHaveSingleItem();
        chd.State.ShouldBe("unsupported");
        chd.ContentGrant.ShouldBeNull();
        chd.Runtime.ShouldBeNull();
        chd.Sha256.ShouldBeNull();
        _grantIssuer.DidNotReceive().IssueDownloadGrant(Arg.Any<ConsumerContentGrant>());
    }

    private static RomDeliveryTarget CreateChdTarget()
    {
        var decoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('4', 40)), 1);
        return new(19, 23, RomDeliveryFormat.Chd, "disc.chd", Sha256.Parse(new string('b', 64)), 4096,
            Sha256.Parse(new string('a', 64)), 8192, Sha256.Parse(new string('b', 64)), 4096, RomRepresentationEncoding.Chd,
            Md5.Parse(new string('c', 32)), Sha1.Parse(new string('d', 40)),
            new RomChdDeliveryBinding(51, 61, Guid.Parse("66666666-6666-6666-6666-666666666666"), decoder));
    }

    private IssueConsumerReleaseManifestCommandHandler CreateHandler(RomDeliveryPolicy? deliveryPolicy = null) =>
        new(_currentUser, _repository, _grantIssuer, _derivedGrants, _serverIdentity, deliveryPolicy ?? TestDeliveryPolicies.Production);

    private void SetResult(ConsumerLibraryReadResult<ConsumerReleaseManifest> result) =>
        _repository.GetManifestAsync(
                new ConsumerReleaseManifestRequest(new ConsumerLibraryScope(UserId), ReleaseId),
                Arg.Any<CancellationToken>())
            .Returns(result);

    private static ConsumerReleaseManifest CreateManifest() =>
        new(
            UserId,
            TitleId,
            PlatformId,
            "snes",
            ReleaseId,
            "Release",
            Revision: null,
            IsComplete: true,
            new ConsumerReleaseRuntime(
                "single_rom",
                new ConsumerLaunchTarget("file", "game.sfc"),
                "direct_files",
                1),
            []);
}
