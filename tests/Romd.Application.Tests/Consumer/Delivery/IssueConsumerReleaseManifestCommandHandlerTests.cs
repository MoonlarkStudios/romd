using NSubstitute;
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

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed class IssueConsumerReleaseManifestCommandHandlerTests
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
        var sha256 = Sha256.FromSpan(new byte[Sha256.ByteLength]);
        var manifest = CreateManifest() with
        {
            Items =
            [
                new ConsumerReleaseManifestItem(
                    RomId: 19,
                    FileId: 23,
                    Sha256: sha256,
                    SizeBytes: 31,
                    ContentSizeBytes: 31,
                    RelativePath: "game.sfc",
                    Role: "rom",
                    IsAvailable: true)
            ]
        };
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseManifest>.Found(LibraryId, manifest));
        _grantIssuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>())
            .Returns(new ConsumerIssuedContentGrant(
                "/delivery/content/token",
                DateTimeOffset.UtcNow.AddMinutes(1)));

        var result = await CreateHandler().HandleAsync(new IssueConsumerReleaseManifestCommand(ReleaseId));

        result.IsError.ShouldBeFalse();
        _grantIssuer.Received(1).IssueDownloadGrant(new ConsumerContentGrant(
            UserId,
            LibraryId,
            TitleId,
            ReleaseId,
            19,
            23,
            sha256,
            31));
    }

    private IssueConsumerReleaseManifestCommandHandler CreateHandler() =>
        new(_currentUser, _repository, _grantIssuer, _serverIdentity);

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
