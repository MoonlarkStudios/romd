using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Activity.Commands.DeletePlaySession;
using Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Activity;
using Romd.Domain.Activity;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Activity;

public sealed class PlayActivityHandlerTests
{
    private const int LibraryId = 12;
    private const int TitleId = 34;
    private const int ReleaseId = 56;
    private static readonly Guid UserId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConsumerReleaseAccessRepository _access = Substitute.For<IConsumerReleaseAccessRepository>();
    private readonly IPlayActivityRepository _activity = Substitute.For<IPlayActivityRepository>();
    private readonly IPlayActivityUnitOfWork _unitOfWork = Substitute.For<IPlayActivityUnitOfWork>();
    private readonly IPlayActivityTransaction _transaction = Substitute.For<IPlayActivityTransaction>();

    public PlayActivityHandlerTests()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(UserId);
        _unitOfWork.BeginAsync(UserId, Arg.Any<CancellationToken>()).Returns(_transaction);
        _access.GetAccessAsync(
                new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(UserId), ReleaseId),
                Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found(
                LibraryId,
                ConsumerReleaseAccessDecision.Allow(TitleId)));
    }

    [Fact]
    public async Task Upsert_Unauthenticated_DoesNotTrustOrWriteRequestData()
    {
        _currentUser.IsAuthenticated.Returns(false);
        _currentUser.UserId.Returns((Guid?)null);

        var result = await CreateUpsertHandler().HandleAsync(new UpsertPlaySessionCommand(SessionId, Request()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentUserRequired().Code);
        await _access.DidNotReceiveWithAnyArgs().GetAccessAsync(default!, default);
        await _activity.DidNotReceiveWithAnyArgs().UpsertAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Upsert_CompletedOfflineSnapshot_AcceptsOpaqueClientAndPrincipalOwner()
    {
        var endedAt = StartedAt.AddMinutes(4);
        _activity.UpsertAsync(UserId, SessionId, Arg.Any<PlaySessionSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var snapshot = call.ArgAt<PlaySessionSnapshot>(2);
                return new PlaySessionUpsertResult(
                    PlaySessionUpsertStatus.Created,
                    new PlaySession(
                        UserId,
                        SessionId,
                        snapshot.ClientId,
                        snapshot.TitleId,
                        snapshot.ReleaseId,
                        snapshot.StartedAt,
                        snapshot.EndedAt,
                        snapshot.ActiveDurationSeconds,
                        StartedAt,
                        endedAt));
            });

        var result = await CreateUpsertHandler().HandleAsync(new UpsertPlaySessionCommand(
            SessionId,
            Request(clientId: "org.example.third-party", endedAt: endedAt, duration: 180)));

        result.IsError.ShouldBeFalse();
        result.Value.ClientId.ShouldBe("org.example.third-party");
        result.Value.EndedAt.ShouldBe(endedAt);
        result.Value.ActiveDurationSeconds.ShouldBe(180);
        await _activity.Received(1).UpsertAsync(
            UserId,
            SessionId,
            Arg.Is<PlaySessionSnapshot>(snapshot =>
                snapshot.ClientId == "org.example.third-party" &&
                snapshot.TitleId == TitleId &&
                snapshot.ReleaseId == ReleaseId &&
                snapshot.EndedAt == endedAt &&
                snapshot.ActiveDurationSeconds == 180),
            Arg.Any<CancellationToken>());
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_EndBeforeStart_IsRejectedBeforeAuthorizationOrPersistence()
    {
        var result = await CreateUpsertHandler().HandleAsync(new UpsertPlaySessionCommand(
            SessionId,
            Request(endedAt: StartedAt.AddSeconds(-1))));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.InvalidPlaySession("").Code);
        await _access.DidNotReceiveWithAnyArgs().GetAccessAsync(default!, default);
        await _activity.DidNotReceiveWithAnyArgs().UpsertAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Upsert_InaccessibleRelease_IsNotPersisted()
    {
        _access.GetAccessAsync(
                new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(UserId), ReleaseId),
                Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found(
                LibraryId,
                ConsumerReleaseAccessDecision.Revoke()));

        var result = await CreateUpsertHandler().HandleAsync(new UpsertPlaySessionCommand(SessionId, Request()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.TitleNotFound().Code);
        await _activity.DidNotReceiveWithAnyArgs().UpsertAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Upsert_ConflictingSnapshot_ReturnsConflict()
    {
        _activity.UpsertAsync(UserId, SessionId, Arg.Any<PlaySessionSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new PlaySessionUpsertResult(PlaySessionUpsertStatus.Conflict, null));

        var result = await CreateUpsertHandler().HandleAsync(new UpsertPlaySessionCommand(SessionId, Request()));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.PlaySessionConflict().Code);
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
    }

    [Fact]
    public async Task Delete_UsesAuthenticatedOwnershipWithoutRequiringCurrentTitleAccess()
    {
        var result = await new DeletePlaySessionCommandHandler(_currentUser, _activity)
            .HandleAsync(new DeletePlaySessionCommand(SessionId));

        result.IsError.ShouldBeFalse();
        await _activity.Received(1).DeleteAsync(UserId, SessionId, Arg.Any<CancellationToken>());
        await _access.DidNotReceiveWithAnyArgs().GetAccessAsync(default!, default);
    }

    private UpsertPlaySessionCommandHandler CreateUpsertHandler() => new(_currentUser, _access, _activity, _unitOfWork);

    private static UpsertPlaySessionRequest Request(
        string clientId = "romd.web",
        DateTimeOffset? endedAt = null,
        int? duration = null) => new()
    {
        ClientId = clientId,
        TitleId = IdCoder.Encode(TitleId),
        ReleaseId = IdCoder.Encode(ReleaseId),
        StartedAt = StartedAt,
        EndedAt = endedAt,
        ActiveDurationSeconds = duration
    };
}
