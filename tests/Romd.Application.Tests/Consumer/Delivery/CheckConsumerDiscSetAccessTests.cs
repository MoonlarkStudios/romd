using ErrorOr;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.CheckConsumerDiscSetAccess;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed class CheckConsumerDiscSetAccessTests
{
    private readonly ICurrentUser user = Substitute.For<ICurrentUser>();
    private readonly IServerInstanceIdentity server = Substitute.For<IServerInstanceIdentity>();
    private readonly IConsumerDiscSetRepository repository = Substitute.For<IConsumerDiscSetRepository>();
    private readonly IConsumerDiscSetManifestBuilder manifests = Substitute.For<IConsumerDiscSetManifestBuilder>();
    private readonly ConsumerDiscSetPlan plan;
    private readonly ConsumerDiscSetAccessRequest request;
    public CheckConsumerDiscSetAccessTests()
    {
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(Guid.NewGuid());
        server.InstanceId.Returns(Guid.NewGuid());
        var release = new ConsumerReleaseManifest(user.UserId!.Value, 7, 9, "psx", 11, "Disc", null, true, new("disc", null, "files", 0), []);
        plan = new(3, Guid.NewGuid(), "Game", null, [new(1, release, "game.cue")]);
        request = new(1, server.InstanceId.ToString("D"), plan.Revision, new string('a', 64), [IdCoder.Encode(11)], [new(IdCoder.Encode(11), null, null)], false);
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(4, plan));
    }
    private Task<ErrorOr<ConsumerDiscSetAccessDto>> Check(ConsumerDiscSetAccessRequest? value = null) =>
        new CheckConsumerDiscSetAccessCommandHandler(user, server, repository, manifests).HandleAsync(new(3, value ?? request));
    [Fact]
    public async Task Access_ExactSnapshot_RevalidatesWithoutIssuingGrantsAndEchoesIdentity()
    {
        manifests.BuildAsync(Arg.Any<IssueConsumerDiscSetManifestCommand>(), Arg.Any<ConsumerDiscSetPlan>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            new ConsumerDiscSetManifestDto(request.ServerInstanceId, IdCoder.Encode(3), plan.Revision, request.DeliverySnapshotId,
                IdCoder.Encode(7), "psx", "Game", null, false, [new(IdCoder.Encode(11), 1, "Disc", "game.cue", [])], new("id", "playlist.m3u", "hash", (Romd.Contracts.Common.Models.ByteCount)0L, "utf-8", "")));
        var result = await Check();
        result.IsError.ShouldBeFalse();
        result.Value.Allowed.ShouldBeTrue();
        result.Value.LayoutVersions.ShouldBe(request.LayoutVersions);
        result.Value.SelectedReleaseIds.ShouldBe(request.SelectedReleaseIds);
        await manifests.Received(1).BuildAsync(Arg.Is<IssueConsumerDiscSetManifestCommand>(x => !x.IssueGrants
            && x.Selection!.RequiredSnapshotId == request.DeliverySnapshotId), plan, 4, user.UserId!.Value, Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Access_DeniedGroup_EchoesIdentityWithoutMetadata()
    {
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound(4));
        var result = await Check();
        result.Value.Allowed.ShouldBeFalse();
        result.Value.TitleId.ShouldBeNull();
        result.Value.SystemKey.ShouldBeNull();
        result.Value.DeliverySnapshotId.ShouldBe(request.DeliverySnapshotId);
        await manifests.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default, default, default);
    }
    [Fact]
    public async Task Access_UnavailableLibraryOrChangedSnapshot_IsConflictNotRevocation()
    {
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable());
        (await Check()).FirstError.Type.ShouldBe(ErrorType.Conflict);
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(4, plan));
        manifests.BuildAsync(Arg.Any<IssueConsumerDiscSetManifestCommand>(), Arg.Any<ConsumerDiscSetPlan>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Error.Conflict("Changed", "Changed bytes"));
        (await Check()).FirstError.Type.ShouldBe(ErrorType.Conflict);
    }
    [Fact]
    public async Task Access_InvalidIdentityConflictsAndChangedPublishedRevisionRevokes()
    {
        (await Check(request with { LayoutVersions = [null!] })).FirstError.Type.ShouldBe(ErrorType.Conflict);
        (await Check(request with { ServerInstanceId = Guid.NewGuid().ToString("D") })).FirstError.Type.ShouldBe(ErrorType.Conflict);
        (await Check(request with { Revision = Guid.NewGuid() })).Value.Allowed.ShouldBeFalse();
        (await Check(request with { SelectedReleaseIds = [IdCoder.Encode(99)] })).FirstError.Type.ShouldBe(ErrorType.Conflict);
    }
    [Fact]
    public async Task Access_CoherentChangedSnapshotRevokesWhileTemporaryMissingMediaDoesNot()
    {
        manifests.BuildAsync(Arg.Any<IssueConsumerDiscSetManifestCommand>(), Arg.Any<ConsumerDiscSetPlan>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Error.Conflict("DiscSet.Snapshot", "Authoritative selected bytes changed"));
        (await Check()).Value.Allowed.ShouldBeFalse();
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(
            new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(4, plan with { Discs = [plan.Discs[0] with { Readiness = "missing-media" }] }));
        (await Check()).FirstError.Type.ShouldBe(ErrorType.Conflict);
    }

    [Theory]
    [InlineData("descriptor-required")]
    [InlineData("missing-media")]
    [InlineData("stale-layout")]
    public async Task Access_AuthoritativeDetachedOrReplacedLayout_RevokesBeforeReadiness(string readiness)
    {
        var installedRevision = Guid.NewGuid();
        var installed = request with { LayoutVersions = [new(IdCoder.Encode(11), IdCoder.Encode(41), installedRevision.ToString("D"))] };
        foreach (var identity in new OriginalDiscLayoutIdentity[] { new(null, null), new(42, Guid.NewGuid()), new(41, Guid.NewGuid()) })
        {
            repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(
                new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(4, plan with
                { Discs = [plan.Discs[0] with { Readiness = readiness, LayoutIdentity = identity }] }));
            var result = await Check(installed);
            result.IsError.ShouldBeFalse();
            result.Value.Allowed.ShouldBeFalse();
            result.Value.LayoutVersions.ShouldBe(installed.LayoutVersions);
            result.Value.TitleId.ShouldBeNull();
            result.Value.SystemKey.ShouldBeNull();
        }
        await manifests.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Access_UnavailableBytesWithUnchangedOrUnknownIdentity_DoesNotRevoke(bool knownIdentity)
    {
        var revision = Guid.NewGuid();
        var installed = request with { LayoutVersions = [new(IdCoder.Encode(11), IdCoder.Encode(41), revision.ToString("D"))] };
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>()).Returns(
            new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(4, plan with
            { Discs = [plan.Discs[0] with { Readiness = "stale-layout", LayoutIdentity = knownIdentity ? new(41, revision) : null }] }));
        (await Check(installed)).FirstError.Code.ShouldBe("DiscSet.AccessUnavailable");
        await manifests.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default, default, default);
    }

}
