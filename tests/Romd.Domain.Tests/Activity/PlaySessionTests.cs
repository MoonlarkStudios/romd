using Romd.Domain.Activity;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Activity;

public sealed class PlaySessionTests
{
    [Fact]
    public void Merge_IdenticalOpenRetry_RemainsUnchanged()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var session = Create(startedAt);

        var result = session.Merge(new PlaySessionSnapshot("romd.web", 1, 2, startedAt, null, null), startedAt.AddMinutes(1));

        result.ShouldBe(PlaySessionMergeResult.Unchanged);
        session.EndedAt.ShouldBeNull();
    }

    [Fact]
    public void Merge_EndAfterStart_ClosesSession()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var endedAt = startedAt.AddMinutes(12);
        var session = Create(startedAt);

        var result = session.Merge(new PlaySessionSnapshot("romd.web", 1, 2, startedAt, endedAt, 600), endedAt);

        result.ShouldBe(PlaySessionMergeResult.Updated);
        session.EndedAt.ShouldBe(endedAt);
        session.ActiveDurationSeconds.ShouldBe(600);
    }

    [Fact]
    public void Merge_LateOpenSnapshot_DoesNotReopenClosedSession()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var endedAt = startedAt.AddMinutes(12);
        var session = Create(startedAt, endedAt, 600);

        var result = session.Merge(new PlaySessionSnapshot("romd.web", 1, 2, startedAt, null, null), endedAt.AddMinutes(1));

        result.ShouldBe(PlaySessionMergeResult.Unchanged);
        session.EndedAt.ShouldBe(endedAt);
    }

    [Theory]
    [InlineData("other.client", 1, 2)]
    [InlineData("romd.web", 9, 2)]
    [InlineData("romd.web", 1, 9)]
    public void Merge_ImmutableIdentityChanges_Conflicts(string clientId, int titleId, int releaseId)
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var session = Create(startedAt);

        var result = session.Merge(new PlaySessionSnapshot(clientId, titleId, releaseId, startedAt, null, null), startedAt);

        result.ShouldBe(PlaySessionMergeResult.Conflict);
    }

    [Fact]
    public void Merge_DifferentNonNullEnding_Conflicts()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var session = Create(startedAt, startedAt.AddMinutes(5), 200);

        var result = session.Merge(
            new PlaySessionSnapshot("romd.web", 1, 2, startedAt, startedAt.AddMinutes(6), 200),
            startedAt.AddMinutes(6));

        result.ShouldBe(PlaySessionMergeResult.Conflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Merge_SubMillisecondFactsRemainExactWithoutPersistence(bool changeEnding)
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234);
        var endedAt = startedAt.AddMinutes(5).AddTicks(2345);
        var session = Create(startedAt, endedAt, 200);
        var snapshot = new PlaySessionSnapshot("romd.web", 1, 2,
            changeEnding ? startedAt : startedAt.AddTicks(1),
            changeEnding ? endedAt.AddTicks(1) : endedAt, 200);

        var result = session.Merge(snapshot, endedAt.AddTicks(17));

        result.ShouldBe(PlaySessionMergeResult.Conflict);
        session.StartedAt.ShouldBe(startedAt);
        session.EndedAt.ShouldBe(endedAt);
        session.CreatedAt.ShouldBe(startedAt);
        session.UpdatedAt.ShouldBe(startedAt);
    }

    [Fact]
    public void Merge_NewEndingRetainsEveryInputTick()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234);
        var endedAt = startedAt.AddMinutes(5).AddTicks(2345);
        var updatedAt = endedAt.AddTicks(17);
        var session = Create(startedAt);

        var result = session.Merge(new PlaySessionSnapshot("romd.web", 1, 2, startedAt, endedAt, 200), updatedAt);

        result.ShouldBe(PlaySessionMergeResult.Updated);
        session.StartedAt.ShouldBe(startedAt);
        session.EndedAt.ShouldBe(endedAt);
        session.CreatedAt.ShouldBe(startedAt);
        session.UpdatedAt.ShouldBe(updatedAt);
    }

    private static PlaySession Create(DateTimeOffset startedAt, DateTimeOffset? endedAt = null, int? duration = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "romd.web", 1, 2, startedAt, endedAt, duration, startedAt, startedAt);
}
