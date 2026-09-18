using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class TriggerTitleEnrichmentCommandHandlerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TriggerEnrichment_NewRequest_CommitsTitleAndJobBeforeEnqueue()
    {
        var order = new List<string>();
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var title = NewTitle(7, enrichmentStatus: EnrichmentStatus.Failed);
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(title);
        jobs.GetActiveForTitleAsync(7, Arg.Any<CancellationToken>())
            .Returns((EnrichmentJob?)null);
        titles.UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "title"));
        jobs.AddStagedAsync(Arg.Any<EnrichmentJob>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.Arg<EnrichmentJob>();
                job.TitleId.ShouldBe(7);
                job.PhaseEnum.ShouldBe(EnrichmentJobPhase.Pending);
                job.CreatedAt.ShouldBe(FixedNow);
                order.Add("job");
                return Task.CompletedTask;
            });
        enqueuer.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "enqueue"));
        var handler = Handler(titles, jobs, enqueuer, unitOfWork);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBe(Guid.Empty);
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Pending);
        order.ShouldBe(["begin", "title", "job", "commit", "dispose", "enqueue"]);
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerEnrichment_ActiveDispatchedJob_RequeuesTitleWithoutNewJobOrEnqueue()
    {
        var order = new List<string>();
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var title = NewTitle(7, enrichmentStatus: EnrichmentStatus.Completed);
        var activeJob = EnrichmentJob.Create("Chrono", 7, 3, new FixedTimeProvider(FixedNow));
        activeJob.SetHangfireJobId("hangfire-dispatched");
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(title);
        jobs.GetActiveForTitleAsync(7, Arg.Any<CancellationToken>()).Returns(activeJob);
        titles.UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "title"));
        var handler = Handler(titles, jobs, enqueuer, unitOfWork);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(activeJob.Id);
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Pending);
        order.ShouldBe(["begin", "title", "commit", "dispose"]);
        await jobs.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_ActivePendingUndispatchedJob_CommitsTitleAndAcceleratesExistingJob()
    {
        var order = new List<string>();
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var title = NewTitle(7, enrichmentStatus: EnrichmentStatus.NotFound);
        var activeJob = EnrichmentJob.Create("Chrono", 7, 3, new FixedTimeProvider(FixedNow));
        var unitOfWork = new RecordingUnitOfWork(order, new RecordingTransaction(order));
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(title);
        jobs.GetActiveForTitleAsync(7, Arg.Any<CancellationToken>()).Returns(activeJob);
        titles.UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "title"));
        enqueuer.EnqueueAsync(activeJob.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Record(order, "enqueue"));
        var handler = Handler(titles, jobs, enqueuer, unitOfWork);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(activeJob.Id);
        order.ShouldBe(["begin", "title", "commit", "dispose", "enqueue"]);
        await jobs.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await enqueuer.Received(1).EnqueueAsync(activeJob.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerEnrichment_MissingTitle_ReturnsNotFoundWithoutJobLookup()
    {
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        titles.GetWithCollectionsAsync(404, Arg.Any<CancellationToken>())
            .Returns((Title?)null);
        var handler = Handler(
            titles,
            jobs,
            Substitute.For<IEnrichmentJobEnqueuer>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])));

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(404));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await jobs.DidNotReceiveWithAnyArgs().GetActiveForTitleAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_PersistenceException_ReturnsOpaqueFailure()
    {
        var titles = Substitute.For<ITitleRepository>();
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task<Title?>>(_ => throw new InvalidOperationException("provider detail"));
        var handler = Handler(
            titles,
            Substitute.For<IEnrichmentJobRepository>(),
            Substitute.For<IEnrichmentJobEnqueuer>(),
            new RecordingUnitOfWork([], new RecordingTransaction([])));

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Enrichment.RequestFailed");
        result.FirstError.Description.ShouldNotContain("provider detail");
    }

    [Fact]
    public async Task TriggerEnrichment_CommitFailure_ReturnsOpaqueFailureWithoutEnqueue()
    {
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(NewTitle(7));
        var unitOfWork = new RecordingUnitOfWork(
            [],
            new RecordingTransaction([], commitException: new InvalidOperationException("commit")));
        var handler = Handler(titles, jobs, enqueuer, unitOfWork);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Enrichment.RequestFailed");
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_PreCommitCancellation_PropagatesWithoutEnqueue()
    {
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(NewTitle(7));
        jobs.AddStagedAsync(Arg.Any<EnrichmentJob>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException("pre-commit"));
        var handler = Handler(
            titles,
            jobs,
            enqueuer,
            new RecordingUnitOfWork([], new RecordingTransaction([])));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new TriggerTitleEnrichmentCommand(7)));

        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_PostCommitDisposeFailure_ReturnsAcceptedAndEnqueues()
    {
        var order = new List<string>();
        var titles = Substitute.For<ITitleRepository>();
        var jobs = Substitute.For<IEnrichmentJobRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var logger = new RecordingLogger<TriggerTitleEnrichmentCommandHandler>();
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(NewTitle(7));
        var unitOfWork = new RecordingUnitOfWork(
            order,
            new RecordingTransaction(order, disposeException: new InvalidOperationException("dispose")));
        var handler = Handler(titles, jobs, enqueuer, unitOfWork, logger);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
        order.IndexOf("commit").ShouldBeLessThan(order.IndexOf("dispose"));
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("transaction cleanup failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TriggerEnrichment_PostCommitEnqueueFailure_ReturnsAcceptedAndWarns(bool cancellation)
    {
        var titles = Substitute.For<ITitleRepository>();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var logger = new RecordingLogger<TriggerTitleEnrichmentCommandHandler>();
        titles.GetWithCollectionsAsync(7, Arg.Any<CancellationToken>()).Returns(NewTitle(7));
        enqueuer.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => cancellation
                ? throw new OperationCanceledException("post-commit")
                : throw new InvalidOperationException("enqueue"));
        var handler = Handler(
            titles,
            Substitute.For<IEnrichmentJobRepository>(),
            enqueuer,
            new RecordingUnitOfWork([], new RecordingTransaction([])),
            logger);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("worker dispatcher will recover", StringComparison.Ordinal));
    }

    private static TriggerTitleEnrichmentCommandHandler Handler(
        ITitleRepository titles,
        IEnrichmentJobRepository jobs,
        IEnrichmentJobEnqueuer enqueuer,
        IUnitOfWork unitOfWork,
        ILogger<TriggerTitleEnrichmentCommandHandler>? logger = null) =>
        new(
            titles,
            jobs,
            enqueuer,
            unitOfWork,
            new FixedTimeProvider(FixedNow),
            logger ?? NullLogger<TriggerTitleEnrichmentCommandHandler>.Instance);

    private static Task Record(ICollection<string> order, string value)
    {
        order.Add(value);
        return Task.CompletedTask;
    }

    private static Title NewTitle(
        int id,
        EnrichmentStatus enrichmentStatus = EnrichmentStatus.None) =>
        Title.Rehydrate(
            id,
            3,
            "Chrono",
            "chrono",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            enrichmentStatus,
            null,
            DateTimeOffset.UtcNow);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingUnitOfWork(
        List<string> order,
        RecordingTransaction transaction) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            order.Add("flush");
            return Task.CompletedTask;
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            order.Add("begin");
            return Task.FromResult<ITransaction>(transaction);
        }
    }

    private sealed class RecordingTransaction(
        List<string> order,
        Exception? commitException = null,
        Exception? disposeException = null) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            order.Add("commit");
            return commitException is null
                ? Task.CompletedTask
                : Task.FromException(commitException);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            order.Add("dispose");
            return disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeException);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}
