using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Systems;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class S5cJobItemExportLeaseTests
{
    [Fact]
    public async Task HandleAsync_PrefetchFails_DisposesSnapshotWithoutCompletion()
    {
        var repository = Substitute.For<IJobItemRepository>();
        var transaction = new RecordingSnapshot();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetByJobAsync(Arg.Any<Guid>(), null, JobItemExport.BatchSize, Arg.Any<CancellationToken>())
            .Returns<Task<JobItemPageResult>>(_ => throw new IOException("read failed"));

        await Should.ThrowAsync<IOException>(() => new ExportJobItemsQueryHandler(repository, snapshots)
            .HandleAsync(new ExportJobItemsQuery(Guid.NewGuid())));

        transaction.Completed.ShouldBeFalse();
        transaction.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_EntireRead_KeepsSnapshotUntilExplicitCompletionAndDisposal()
    {
        var transaction = new RecordingSnapshot();
        var item = Item();
        var export = new JobItemExport(Substitute.For<IJobItemRepository>(), transaction, item.JobId, new JobItemPageResult([item], false));
        var read = new List<JobItemView>();
        await foreach (var value in export.ReadAsync())
            read.Add(value);

        read.ShouldHaveSingleItem().ShouldBeSameAs(item);
        transaction.Completed.ShouldBeFalse();
        transaction.DisposeCount.ShouldBe(0);
        await export.CompleteAsync();
        transaction.Completed.ShouldBeTrue();
        await export.DisposeAsync();
        await export.DisposeAsync();
        transaction.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_StoppedEarly_CannotCompleteAndDisposesOnce()
    {
        var transaction = new RecordingSnapshot();
        var item = Item();
        var export = new JobItemExport(Substitute.For<IJobItemRepository>(), transaction, item.JobId, new JobItemPageResult([item, Item()], false));
        await foreach (var _ in export.ReadAsync())
            break;

        await Should.ThrowAsync<InvalidOperationException>(() => export.CompleteAsync());
        await export.DisposeAsync();
        transaction.Completed.ShouldBeFalse();
        transaction.DisposeCount.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in export.ReadAsync())
            { }
        });
    }

    [Fact]
    public async Task ReadAsync_SecondEnumeration_IsRejected()
    {
        await using var export = new JobItemExport(Substitute.For<IJobItemRepository>(), new RecordingSnapshot(), Guid.NewGuid(), new JobItemPageResult([], false));
        await foreach (var _ in export.ReadAsync())
        { }
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in export.ReadAsync())
            { }
        });
    }

    private static JobItemView Item() => new()
    {
        Id = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        Kind = JobItemKind.Rom,
        FileName = "file.bin",
        SizeBytes = 7,
        Outcome = JobItemOutcome.Ingested,
        MatchedTitles = [],
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class RecordingSnapshot : IReadSnapshotTransaction
    {
        public bool Completed { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? CompleteFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (CompleteFailure is not null)
                throw CompleteFailure;
            Completed = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    [Theory]
    [InlineData("prefetch", false)]
    [InlineData("prefetch", true)]
    [InlineData("page", false)]
    [InlineData("page", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    public async Task Export_PrimaryAndSnapshotDisposalFault_PreservesOriginalObjectAndToken(string phase, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original snapshot cancellation", cancellation.Token)
            : new IOException("Original snapshot read failure");
        var snapshot = new RecordingSnapshot
        {
            CompleteFailure = phase == "complete" ? primary : null,
            DisposeFailure = new IOException("Secondary snapshot disposal failure")
        };
        var repository = Substitute.For<IJobItemRepository>();
        var factory = Substitute.For<IReadSnapshotTransactionFactory>();
        factory.BeginAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var item = Item();
        var position = new JobItemPosition(item.CreatedAt, item.Id);
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>())
            .Returns(_ => phase == "prefetch" ? Task.FromException<JobItemPageResult>(primary)
                : Task.FromResult(new JobItemPageResult([item], phase == "page", phase == "page" ? position : null)));
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>(), position)
            .Returns(Task.FromException<JobItemPageResult>(primary));

        var observed = await Record.ExceptionAsync(async () =>
        {
            var result = await new ExportJobItemsQueryHandler(repository, factory).HandleAsync(new(item.JobId), cancellation.Token);
            await using var export = result.Value;
            await foreach (var _ in export.ReadAsync(cancellation.Token))
            { }
            await export.CompleteAsync(cancellation.Token);
        });

        observed.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReadSnapshot.CleanupFailure"].ShouldBeSameAs(snapshot.DisposeFailure);
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Export_AcknowledgedCompletionAndSnapshotDisposalFault_RetainsSuccessAndDisposesOnce()
    {
        var snapshot = new RecordingSnapshot { DisposeFailure = new IOException("Secondary snapshot disposal failure") };
        var export = new JobItemExport(Substitute.For<IJobItemRepository>(), snapshot, Guid.NewGuid(), new([], false));
        await foreach (var _ in export.ReadAsync())
        { }

        await export.CompleteAsync();
        await export.DisposeAsync();
        await export.DisposeAsync();

        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("page", false)]
    [InlineData("page", true)]
    [InlineData("keys", false)]
    [InlineData("keys", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    public async Task GetItems_PrimaryAndSnapshotDisposalFault_PreservesReadOrCompletionFailure(string phase, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original item-page cancellation", cancellation.Token)
            : new IOException("Original item-page read failure");
        var snapshot = new RecordingSnapshot
        {
            CompleteFailure = phase == "complete" ? primary : null,
            DisposeFailure = new IOException("Secondary item-page snapshot disposal failure")
        };
        var (handler, job) = ListingHandler(snapshot, phase, primary);

        var observed = await Record.ExceptionAsync(() => handler.HandleAsync(new(job.Id), cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReadSnapshot.CleanupFailure"].ShouldBeSameAs(snapshot.DisposeFailure);
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetItems_AcknowledgedCompletionAndDisposalFault_ReturnsPreparedPage()
    {
        var snapshot = new RecordingSnapshot { DisposeFailure = new IOException("Secondary item-page cleanup failure") };
        var (handler, job) = ListingHandler(snapshot);

        var result = await handler.HandleAsync(new(job.Id));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
    }

    private static (GetJobItemsQueryHandler Handler, UploadJob Job) ListingHandler(RecordingSnapshot snapshot,
        string? phase = null, Exception? primary = null)
    {
        var owner = Guid.NewGuid();
        var job = UploadJob.Create("snapshot.zip", DateTimeOffset.UtcNow, createdByUserId: owner);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(owner);
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var repository = Substitute.For<IJobItemRepository>();
        repository.GetByJobAsync(job.Id, null, 100, Arg.Any<CancellationToken>(), null, null)
            .Returns(_ => phase == "page" ? Task.FromException<JobItemPageResult>(primary!) : Task.FromResult(new JobItemPageResult([], false)));
        var factory = Substitute.For<IReadSnapshotTransactionFactory>();
        factory.BeginAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var keys = Substitute.For<IReferenceCatalogService>();
        keys.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(_ => phase == "keys"
            ? Task.FromException<SystemKeys>(primary!) : Task.FromResult(new SystemKeys(new Dictionary<int, string>())));
        return (new GetJobItemsQueryHandler(repository, factory, jobs, user, keys), job);
    }
}
