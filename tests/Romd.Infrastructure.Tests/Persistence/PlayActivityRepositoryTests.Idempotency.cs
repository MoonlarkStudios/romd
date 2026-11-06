using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Activity;
using Romd.Domain.Activity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class PlayActivityRepositoryTests
{
    [Fact]
    public async Task Upsert_SubMillisecondReplayAndClosureAcrossFreshContexts_PreserveStoredIdentityAndRawNewFacts()
    {
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234);
        var endedAt = startedAt.AddMinutes(5).AddTicks(2345);
        var open = Snapshot(101, 201, startedAt);

        var created = await FreshUpsertAsync(UserId, id, open);
        created.Status.ShouldBe(PlaySessionUpsertStatus.Created);
        created.Session.ShouldNotBeNull().StartedAt.ShouldBe(startedAt);
        var replay = await FreshUpsertAsync(UserId, id, open);
        replay.Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);
        replay.Session.ShouldNotBeNull().StartedAt.ShouldBe(StoredTime(startedAt));
        var closed = open with { EndedAt = endedAt, ActiveDurationSeconds = 240 };
        var updated = await FreshUpsertAsync(UserId, id, closed);
        updated.Status.ShouldBe(PlaySessionUpsertStatus.Updated);
        updated.Session.ShouldNotBeNull().EndedAt.ShouldBe(endedAt);
        var closedReplay = await FreshUpsertAsync(UserId, id, closed);
        closedReplay.Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);
        closedReplay.Session.ShouldNotBeNull().EndedAt.ShouldBe(StoredTime(endedAt));
        (await FreshUpsertAsync(UserId, id, open)).Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);

        PlaySessionSnapshot[] conflicts =
        [
            closed with { StartedAt = startedAt.AddMilliseconds(1) },
            closed with { EndedAt = endedAt.AddMilliseconds(1) },
            closed with { ClientId = "different.client" },
            closed with { TitleId = 102, ReleaseId = 202 },
            closed with { ActiveDurationSeconds = 241 }
        ];
        foreach (var conflict in conflicts)
            (await FreshUpsertAsync(UserId, id, conflict)).Status.ShouldBe(PlaySessionUpsertStatus.Conflict);

        await using var read = _database.CreateContext();
        var stored = (await read.PlaySessions.ToListAsync()).ShouldHaveSingleItem();
        stored.StartedAt.ShouldBe(StoredTime(startedAt));
        stored.EndedAt.ShouldBe(StoredTime(endedAt));
        stored.ActiveDurationSeconds.ShouldBe(240);
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("closure")]
    [InlineData("conflict")]
    public async Task Upsert_OverlappingFirstDeliveries_WaitsBeforeCommitThenUsesTheCommittedSnapshot(string delivery)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234);
        var open = Snapshot(101, 201, startedAt);
        var next = delivery switch
        {
            "closure" => open with { EndedAt = startedAt.AddMinutes(5).AddTicks(2345), ActiveDurationSeconds = 240 },
            "conflict" => open with { StartedAt = startedAt.AddMilliseconds(1) },
            _ => open
        };
        await using var first = _database.CreateContext();
        await using var owner = await new PlayActivityUnitOfWork(first).BeginAsync(UserId, deadline.Token);
        (await Repository(first).UpsertAsync(UserId, id, open, deadline.Token)).Status.ShouldBe(PlaySessionUpsertStatus.Created);
        await first.SaveChangesAsync(deadline.Token);
        await using var before = _database.CreateContext();
        (await before.PlaySessions.CountAsync(deadline.Token)).ShouldBe(0);

        await using var second = _database.CreateContext();
        var (pending, waited) = await _database.StartWaitingForALockAsync(second,
            token => UpsertAsync(second, Repository(second), UserId, id, next, token), deadline.Token);
        Exception? primary = null;
        try
        {
            waited.ShouldBeTrue("The second delivery must reach PostgreSQL's lock queue before the first commits.");
            pending.IsCompleted.ShouldBeFalse();
            await owner.CommitAsync(deadline.Token);

            var result = await pending;

            result.Status.ShouldBe(delivery switch
            {
                "closure" => PlaySessionUpsertStatus.Updated,
                "conflict" => PlaySessionUpsertStatus.Conflict,
                _ => PlaySessionUpsertStatus.Unchanged
            });
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(deadline, primary, pending);
        }
        await using var read = _database.CreateContext();
        var stored = (await read.PlaySessions.ToListAsync()).ShouldHaveSingleItem();
        stored.UserId.ShouldBe(UserId);
        stored.SessionId.ShouldBe(id);
        stored.StartedAt.ShouldBe(StoredTime(startedAt));
        stored.EndedAt.ShouldBe(delivery == "closure" ? StoredTime(next.EndedAt.GetValueOrDefault()) : null);
        stored.ActiveDurationSeconds.ShouldBe(delivery == "closure" ? 240 : null);
    }

    [Fact]
    public async Task Upsert_RolledBackFirstDelivery_UnblocksAWaitingDeliveryWithoutPublishingItsSnapshot()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid();
        var original = Snapshot(101, 201, DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234));
        var next = original with { StartedAt = original.StartedAt.AddMilliseconds(1) };
        await using var first = _database.CreateContext();
        await using var owner = await new PlayActivityUnitOfWork(first).BeginAsync(UserId, deadline.Token);
        await Repository(first).UpsertAsync(UserId, id, original, deadline.Token);
        await first.SaveChangesAsync(deadline.Token);
        await using var second = _database.CreateContext();
        var (pending, waited) = await _database.StartWaitingForALockAsync(second,
            token => UpsertAsync(second, Repository(second), UserId, id, next, token), deadline.Token);
        Exception? primary = null;
        try
        {
            waited.ShouldBeTrue();
            pending.IsCompleted.ShouldBeFalse();
            await owner.DisposeAsync();
            first.ChangeTracker.Entries().ShouldBeEmpty();

            var result = await pending;

            result.Status.ShouldBe(PlaySessionUpsertStatus.Created);
            result.Session.ShouldNotBeNull().StartedAt.ShouldBe(next.StartedAt);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(deadline, primary, pending);
        }
        await using var read = _database.CreateContext();
        (await read.PlaySessions.SingleAsync()).StartedAt.ShouldBe(StoredTime(next.StartedAt));
    }

    [Fact]
    public async Task Upsert_UserLockDoesNotBlockAnotherOwner_AndOtherOwnersCannotDeleteItsSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid();
        var open = Snapshot(101, 201, DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234));
        await using var first = _database.CreateContext();
        await using var owner = await new PlayActivityUnitOfWork(first).BeginAsync(UserId, deadline.Token);
        await Repository(first).UpsertAsync(UserId, id, open, deadline.Token);
        await first.SaveChangesAsync(deadline.Token);
        await using var other = _database.CreateContext();

        var independent = await UpsertAsync(other, Repository(other), OtherUserId, id,
            Snapshot(102, 202, open.StartedAt.AddMinutes(1)), deadline.Token);

        independent.Status.ShouldBe(PlaySessionUpsertStatus.Created);
        await owner.CommitAsync(deadline.Token);
        await Repository(other).DeleteAsync(OtherUserId, id, deadline.Token);
        await using var read = _database.CreateContext();
        var retained = (await read.PlaySessions.ToListAsync(deadline.Token)).ShouldHaveSingleItem();
        retained.UserId.ShouldBe(UserId);
        retained.StartedAt.ShouldBe(StoredTime(open.StartedAt));
    }

    [Fact]
    public async Task Upsert_CancelledWhileWaiting_ReleasesItsTransactionWithoutAWriteAndCanRetry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var id = Guid.NewGuid();
        var open = Snapshot(101, 201, DateTimeOffset.Parse("2026-09-14T10:00:00Z").AddTicks(1234));
        await using var first = _database.CreateContext();
        await using var owner = await new PlayActivityUnitOfWork(first).BeginAsync(UserId, deadline.Token);
        await Repository(first).UpsertAsync(UserId, id, open, deadline.Token);
        await first.SaveChangesAsync(deadline.Token);
        await using var second = _database.CreateContext();
        int processId = await second.OpenBackendProcessIdAsync(deadline.Token);
        var pending = UpsertAsync(second, Repository(second), UserId, id, open, cancellation.Token);
        Exception? primary = null;
        try
        {
            (await _database.WaitsForALockAsync(processId, pending, deadline.Token)).ShouldBeTrue();
            await cancellation.CancelAsync();
            var failure = (await Record.ExceptionAsync(async () => await pending))
                .ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull();
            failure.CancellationToken.ShouldBe(cancellation.Token);
            second.Database.CurrentTransaction.ShouldBeNull();
            second.ChangeTracker.Entries().ShouldBeEmpty();
            await using var observer = _database.CreateContext();
            (await observer.PlaySessions.CountAsync(deadline.Token)).ShouldBe(0);
            await owner.CommitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, primary, pending);
        }
        (await FreshUpsertAsync(UserId, id, open)).Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);
    }

    private async Task<PlaySessionUpsertResult> FreshUpsertAsync(Guid userId, Guid id, PlaySessionSnapshot snapshot)
    {
        await using var db = _database.CreateContext();
        return await UpsertAsync(db, Repository(db), userId, id, snapshot);
    }

    private static DateTimeOffset StoredTime(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}
