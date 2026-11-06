using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Activity;
using Romd.Domain.Activity;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class PlayActivityRepositoryTests
{
    [Theory]
    [InlineData("mutation", false)]
    [InlineData("mutation", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("acknowledgement", false)]
    [InlineData("acknowledgement", true)]
    public async Task Upsert_PrimaryAndDisposalFail_PreservesCauseAndRetryUsesDurableState(string phase, bool cancel)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel
            ? new OperationCanceledException("Interrupted play-session write", cancellation.Token)
            : new IOException("Interrupted play-session write");
        var work = new InterruptedActivityWork(new PlayActivityUnitOfWork(db), phase, primary, cancellation, cancel);
        var repository = Repository(db);
        var activity = Substitute.For<IPlayActivityRepository>();
        activity.UpsertAsync(UserId, Arg.Any<Guid>(), Arg.Any<PlaySessionSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var result = await repository.UpsertAsync(UserId, call.ArgAt<Guid>(1), call.ArgAt<PlaySessionSnapshot>(2),
                    call.ArgAt<CancellationToken>(3));
                if (phase == "mutation")
                {
                    await db.SaveChangesAsync(call.ArgAt<CancellationToken>(3));
                    if (cancel)
                        await cancellation.CancelAsync();
                    throw primary;
                }
                return result;
            });
        var logger = Substitute.For<ILogger<UpsertPlaySessionCommandHandler>>();
        var original = ActivityCommand();
        var command = original with
        {
            Request = original.Request with
            {
                StartedAt = original.Request.StartedAt.AddTicks(1234),
                EndedAt = original.Request.StartedAt.AddMinutes(5).AddTicks(5678),
                ActiveDurationSeconds = 240
            }
        };

        var actual = await Record.ExceptionAsync(() => ActivityHandler(activity, work, logger).HandleAsync(command, cancellation.Token));

        actual.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)actual!).CancellationToken.ShouldBe(cancellation.Token);
        work.Disposals.ShouldBe(1);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log" &&
            ReferenceEquals(call.GetArguments()[3], work.CleanupFailure));
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var verify = _database.CreateContext();
        (await verify.PlaySessions.CountAsync()).ShouldBe(phase == "acknowledgement" ? 1 : 0);

        await using var retryDb = _database.CreateContext();
        var retry = await ActivityHandler(Repository(retryDb), new PlayActivityUnitOfWork(retryDb), logger).HandleAsync(command);

        retry.IsError.ShouldBeFalse();
        retry.Value.SessionId.ShouldBe(command.SessionId);
        (await verify.PlaySessions.CountAsync()).ShouldBe(1);
        var stored = await verify.PlaySessions.SingleAsync();
        stored.StartedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(command.Request.StartedAt.ToUnixTimeMilliseconds()));
        stored.EndedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(command.Request.EndedAt.GetValueOrDefault().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Upsert_AcknowledgedCommitAndDisposalFail_ReturnsDurableSession()
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptedActivityWork(new PlayActivityUnitOfWork(db), "none", new IOException(), cancellation, false);
        var logger = Substitute.For<ILogger<UpsertPlaySessionCommandHandler>>();
        var command = ActivityCommand();

        var result = await ActivityHandler(Repository(db), work, logger).HandleAsync(command);

        result.IsError.ShouldBeFalse();
        result.Value.SessionId.ShouldBe(command.SessionId);
        work.Disposals.ShouldBe(1);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log" &&
            ReferenceEquals(call.GetArguments()[3], work.CleanupFailure));
        await using var verify = _database.CreateContext();
        (await verify.PlaySessions.SingleAsync()).SessionId.ShouldBe(command.SessionId);
    }

    private UpsertPlaySessionCommandHandler ActivityHandler(IPlayActivityRepository repository, IPlayActivityUnitOfWork work,
        ILogger<UpsertPlaySessionCommandHandler> logger)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(UserId);
        var access = Substitute.For<IConsumerReleaseAccessRepository>();
        access.GetAccessAsync(Arg.Any<ConsumerReleaseAccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found(_libraryId, ConsumerReleaseAccessDecision.Allow(101)));
        return new UpsertPlaySessionCommandHandler(user, access, repository, work, logger);
    }

    private static UpsertPlaySessionCommand ActivityCommand() => new(Guid.NewGuid(), new UpsertPlaySessionRequest
    {
        ClientId = "org.example.client",
        TitleId = IdCoder.Encode(101),
        ReleaseId = IdCoder.Encode(201),
        StartedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z")
    });

    private sealed class InterruptedActivityWork(IPlayActivityUnitOfWork inner, string phase, Exception primary,
        CancellationTokenSource cancellation, bool cancel) : IPlayActivityUnitOfWork
    {
        public IOException CleanupFailure { get; } = new("Secondary play-session cleanup failure");
        public int Disposals { get; private set; }
        public async Task<IPlayActivityTransaction> BeginAsync(Guid userId, CancellationToken ct = default) =>
            new Transaction(this, await inner.BeginAsync(userId, ct));

        private async Task CommitAsync(IPlayActivityTransaction transaction, CancellationToken ct)
        {
            if (phase != "commit")
                await transaction.CommitAsync(ct);
            if (phase is "commit" or "acknowledgement")
            {
                if (cancel)
                    await cancellation.CancelAsync();
                throw primary;
            }
        }

        private sealed class Transaction(InterruptedActivityWork owner, IPlayActivityTransaction transaction) : IPlayActivityTransaction
        {
            public Task CommitAsync(CancellationToken ct = default) => owner.CommitAsync(transaction, ct);
            public async ValueTask DisposeAsync()
            {
                owner.Disposals++;
                await transaction.DisposeAsync();
                throw owner.CleanupFailure;
            }
        }
    }
}
