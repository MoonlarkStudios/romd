using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class EnrichmentAcceptanceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PendingWork_ReturnsSuccessWithoutCreatingLookingUpPlatformReadingClockOrCommitting(bool bulk)
    {
        var context = new Context();
        context.Single.LockAndHasPendingForTitleStagedAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        var existing = Guid.NewGuid();
        context.Bulk.LockAndGetPendingForPlatformStagedAsync(3, Arg.Any<CancellationToken>()).Returns(existing);
        using var cancellation = new CancellationTokenSource();

        var result = await context.HandleAsync(bulk, cancellation.Token);

        result.IsError.ShouldBeFalse();
        if (bulk)
            result.Value.ShouldBe(existing);
        context.Created.ShouldBeNull();
        context.Clock.Calls.ShouldBe(0);
        context.Platforms.ReceivedCalls().ShouldBeEmpty();
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
        context.Committed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NewWork_PreservesCapturedFactsAndCommitsBeforeReturning(bool bulk)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();

        var result = await context.HandleAsync(bulk, cancellation.Token);

        result.IsError.ShouldBeFalse();
        var job = context.Created.ShouldNotBeNull();
        job.CreatedAt.ShouldBe(Now);
        job.PlatformId.ShouldBe(3);
        job.Id.ShouldNotBe(Guid.Empty);
        if (bulk)
        {
            var created = job.ShouldBeOfType<BulkEnrichmentJob>();
            created.Scope.ShouldBe(EnrichmentScope.All);
            created.CreatedByUserId.ShouldBe(context.Actor);
            created.SourceFilename.ShouldBe("snes");
            result.Value.ShouldBe(created.Id);
        }
        else
        {
            var created = job.ShouldBeOfType<EnrichmentJob>();
            created.TitleId.ShouldBe(7);
            created.SourceFilename.ShouldBe("Captured retry name");
            created.CreatedByUserId.ShouldBeNull();
            created.ArtworkOnly.ShouldBeFalse();
            context.Platforms.ReceivedCalls().ShouldBeEmpty();
        }
        context.Events.ShouldBe(bulk ? ["begin", "bulk-lock", "platform", "stage", "commit", "dispose"]
            : ["begin", "title-lock", "stage", "commit", "dispose"]);
        context.Clock.Calls.ShouldBe(1);
        context.Committed.ShouldBeTrue();
        await context.Unit.Received(1).BeginTransactionAsync(cancellation.Token);
        await context.Transaction.Received(1).CommitAsync(cancellation.Token);
    }

    [Fact]
    public async Task Handle_BulkMissingPlatform_PreservesFallbackNameDefaultScopeAndNullableActor()
    {
        var context = new Context();
        context.Platforms.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns((Platform?)null);

        var result = await context.BulkHandler.HandleAsync(new(3));

        result.IsError.ShouldBeFalse();
        var job = context.Created.ShouldBeOfType<BulkEnrichmentJob>();
        job.SourceFilename.ShouldBe("Bulk Enrichment");
        job.Scope.ShouldBe(EnrichmentScope.Tracked);
        job.CreatedByUserId.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CommitStillPending_DoesNotReturnAcceptanceEarly(bool bulk)
    {
        var context = new Context();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AfterStage = _ => { staged.SetResult(); return Task.CompletedTask; };
        context.BeforeCommit = commit.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var pending = context.HandleAsync(bulk, cancellation.Token);
        Exception? primary = null;
        try
        {
            var ready = await Task.WhenAny(staged.Task, pending).WaitAsync(cancellation.Token);
            if (ready == pending)
                await pending;
            pending.IsCompleted.ShouldBeFalse();
            context.Committed.ShouldBeFalse();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            commit.TrySetResult();
            try
            {
                await pending;
            }
            catch when (primary is not null)
            {
                // Observe the owned task without replacing the assertion or cancellation failure.
            }
        }
        (await pending).IsError.ShouldBeFalse();
        context.Committed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_StageFailureOrCancellation_PreservesCauseAndDoesNotCommit(bool bulk, bool cancel)
    {
        var context = new Context();
        Exception cause = cancel ? new OperationCanceledException() : new IOException("staging failed");
        context.AfterStage = _ => Task.FromException(cause);

        var error = await CaptureAsync(() => context.HandleAsync(bulk));

        error.ShouldBeSameAs(cause);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await context.Transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancellationAfterStaging_DoesNotCommitAndPreservesToken(bool bulk)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        context.AfterStage = _ => { cancellation.Cancel(); return Task.CompletedTask; };

        var error = await CaptureAsync(() => context.HandleAsync(bulk, cancellation.Token));

        error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CleanupFaultAfterCommit_ReturnsDurableSuccess(bool bulk)
    {
        var context = new Context();
        context.Transaction.DisposeAsync().Returns(new ValueTask(Task.FromException(new IOException("cleanup failed"))));

        var result = await context.HandleAsync(bulk);

        result.IsError.ShouldBeFalse();
        context.Committed.ShouldBeTrue();
        if (bulk)
            result.Value.ShouldBe(context.Created.ShouldNotBeNull().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CleanupFaultOnSuccessfulNoOp_PreservesFailureWithoutCommit(bool bulk)
    {
        var context = new Context();
        context.Single.LockAndHasPendingForTitleStagedAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        context.Bulk.LockAndGetPendingForPlatformStagedAsync(3, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var cause = new IOException("no-op cleanup failed");
        context.Transaction.DisposeAsync().Returns(new ValueTask(Task.FromException(cause)));

        var error = await CaptureAsync(() => context.HandleAsync(bulk));

        error.ShouldBeSameAs(cause);
        await context.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Handle_PrimaryAndCleanupFault_PreservesExactCauseWithoutRetry(bool bulk, bool atCommit, bool cancel)
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("primary cancellation", cancellation.Token)
            : new IOException("primary acceptance fault");
        if (atCommit)
            context.BeforeCommit = Task.FromException(cause);
        else
            context.AfterStage = _ => Task.FromException(cause);
        context.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));

        var error = await CaptureAsync(() => context.HandleAsync(bulk, cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        await context.Unit.Received(1).BeginTransactionAsync(cancellation.Token);
        await context.Transaction.Received(atCommit ? 1 : 0).CommitAsync(cancellation.Token);
        await context.Transaction.Received(1).DisposeAsync();
        context.Committed.ShouldBeFalse();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Context
    {
        public IEnrichmentJobAcceptance Single { get; } = Substitute.For<IEnrichmentJobAcceptance>();
        public IBulkEnrichmentJobAcceptance Bulk { get; } = Substitute.For<IBulkEnrichmentJobAcceptance>();
        public IPlatformRepository Platforms { get; } = Substitute.For<IPlatformRepository>();
        public IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public List<string> Events { get; } = [];
        public Guid Actor { get; } = Guid.NewGuid();
        public Job? Created { get; private set; }
        public bool Inside { get; private set; }
        public bool Committed { get; private set; }
        public CountingClock Clock { get; }
        public Func<CancellationToken, Task>? AfterStage { get; set; }
        public Task BeforeCommit { get; set; } = Task.CompletedTask;
        public QueueTitleEnrichmentJobCommandHandler SingleHandler { get; }
        public QueueBulkEnrichmentJobCommandHandler BulkHandler { get; }

        public Context()
        {
            Clock = new(() => Inside.ShouldBeTrue());
            Unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            { Inside = true; Events.Add("begin"); return Transaction; });
            Single.LockAndHasPendingForTitleStagedAsync(7, Arg.Any<CancellationToken>()).Returns(_ =>
            { Inside.ShouldBeTrue(); Events.Add("title-lock"); return false; });
            Bulk.LockAndGetPendingForPlatformStagedAsync(3, Arg.Any<CancellationToken>()).Returns(_ =>
            { Inside.ShouldBeTrue(); Events.Add("bulk-lock"); return (Guid?)null; });
            Platforms.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(_ =>
            { Inside.ShouldBeTrue(); Events.Add("platform"); return Platform.CreateNew("Super Nintendo", "snes", Now); });
            Single.AddStagedAsync(Arg.Any<EnrichmentJob>(), Arg.Any<CancellationToken>()).Returns(call => StageAsync(call.Arg<EnrichmentJob>(), call.Arg<CancellationToken>()));
            Bulk.AddStagedAsync(Arg.Any<BulkEnrichmentJob>(), Arg.Any<CancellationToken>()).Returns(call => StageAsync(call.Arg<BulkEnrichmentJob>(), call.Arg<CancellationToken>()));
            Transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            { Events.Add("commit"); await BeforeCommit.WaitAsync(call.Arg<CancellationToken>()); Committed = true; });
            Transaction.DisposeAsync().Returns(_ =>
            { Inside = false; Events.Add("dispose"); return ValueTask.CompletedTask; });
            SingleHandler = new(Single, Unit, Clock, NullLogger<QueueTitleEnrichmentJobCommandHandler>.Instance);
            BulkHandler = new(Bulk, Platforms, Unit, Clock, NullLogger<QueueBulkEnrichmentJobCommandHandler>.Instance);
        }

        private async Task StageAsync(Job job, CancellationToken ct)
        {
            Inside.ShouldBeTrue();
            Created = job;
            Events.Add("stage");
            if (AfterStage is not null)
                await AfterStage(ct);
        }

        public async Task<ErrorOr<object>> HandleAsync(bool bulk, CancellationToken ct = default)
        {
            if (bulk)
            {
                var result = await BulkHandler.HandleAsync(new(3, EnrichmentScope.All, Actor), ct);
                return result.IsError ? result.Errors : ErrorOrFactory.From<object>(result.Value);
            }
            var single = await SingleHandler.HandleAsync(new(7, "Captured retry name", 3), ct);
            return single.IsError ? single.Errors : ErrorOrFactory.From<object>(single.Value);
        }
    }

    private sealed class CountingClock(Action observe) : TimeProvider
    {
        public int Calls { get; private set; }
        public override DateTimeOffset GetUtcNow() { observe(); Calls++; return Now; }
    }
}
