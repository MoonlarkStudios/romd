using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Access.Queries.GetConsumerReleaseAccess;
using Romd.Consumer.Application.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Access;

public sealed class GetConsumerReleaseAccessQueryHandlerTests
{
    private const int LibraryId = 12;
    private const int ReleaseId = 34;
    private const int TitleId = 56;
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServerInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConsumerReleaseAccessRepository _repository =
        Substitute.For<IConsumerReleaseAccessRepository>();
    private readonly IServerInstanceIdentity _serverIdentity = Substitute.For<IServerInstanceIdentity>();

    public GetConsumerReleaseAccessQueryHandlerTests()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(UserId);
        _currentUser.LibraryId.Returns(999);
        _serverIdentity.InstanceId.Returns(ServerInstanceId);
    }

    [Fact]
    public async Task HandleAsync_Unauthenticated_ReturnsCurrentUserRequired()
    {
        _currentUser.IsAuthenticated.Returns(false);
        _currentUser.UserId.Returns((Guid?)null);

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentUserRequired().Code);
        await _repository.DidNotReceiveWithAnyArgs().GetAccessAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_Allowed_MapsExactCanonicalIdentitiesFromLiveScope()
    {
        SetRead(ConsumerReleaseAccessDecision.Allow(TitleId));

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.ServerInstanceId.ShouldBe(ServerInstanceId.ToString("D"));
        result.Value.ReleaseId.ShouldBe(IdCoder.Encode(ReleaseId));
        result.Value.Allowed.ShouldBeTrue();
        result.Value.TitleId.ShouldBe(IdCoder.Encode(TitleId));
        await _repository.Received(1).GetAccessAsync(
            new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(UserId), ReleaseId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Revoked_OmitsTitleIdentity()
    {
        SetRead(ConsumerReleaseAccessDecision.Revoke());

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.Allowed.ShouldBeFalse();
        result.Value.TitleId.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_NoCurrentValidMaterializedLibrary_ReturnsConflict()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.LibraryUnavailable());

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentLibraryUnavailable().Code);
    }

    [Fact]
    public async Task HandleAsync_InconsistentProjection_ReturnsConflictInsteadOfRevocation()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent());

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentLibraryUnavailable().Code);
    }

    [Fact]
    public async Task HandleAsync_ItemNotFoundImpossibleAccessOutcome_ReturnsConflictInsteadOfRevocation()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ItemNotFound(LibraryId));

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentLibraryUnavailable().Code);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, TitleId)]
    public async Task HandleAsync_InvalidDecisionShape_ReturnsConflict(bool allowed, int? titleId)
    {
        SetRead(new ConsumerReleaseAccessDecision(allowed, titleId));

        var result = await CreateHandler().HandleAsync(new GetConsumerReleaseAccessQuery(ReleaseId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentLibraryUnavailable().Code);
    }

    private GetConsumerReleaseAccessQueryHandler CreateHandler() =>
        new(_currentUser, _repository, _serverIdentity);

    private void SetRead(ConsumerReleaseAccessDecision decision) =>
        SetResult(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found(LibraryId, decision));

    private void SetResult(ConsumerLibraryReadResult<ConsumerReleaseAccessDecision> result) =>
        _repository
            .GetAccessAsync(
                new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(UserId), ReleaseId),
                Arg.Any<CancellationToken>())
            .Returns(result);
}
