using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class JobItemQueryHandlerTests
{
    [Fact]
    public async Task GetJobItems_HandleAsync_OwnsAndCommitsReadSnapshot()
    {
        var repository = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var transaction = Substitute.For<IReadSnapshotTransaction>();
        var jobs = Substitute.For<IJobRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: user.UserId);
        jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetByJobAsync(job.Id, null, 25, Arg.Any<CancellationToken>(), null, null).Returns(new JobItemPageResult([], false));
        var handler = new GetJobItemsQueryHandler(repository, snapshots, jobs, user, TestSystemCatalog.Create());

        var result = await handler.HandleAsync(new GetJobItemsQuery(job.Id, null, 25));

        result.Value.Items.ShouldBeEmpty();
        result.Value.HasNextPage.ShouldBeFalse();
        await snapshots.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportJobItems_HandleAsync_TransfersSnapshotLifetimeUntilConsumerCompletes()
    {
        var repository = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var transaction = Substitute.For<IReadSnapshotTransaction>();
        var jobId = Guid.NewGuid();
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetByJobAsync(jobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>()).Returns(new JobItemPageResult([], false));
        var handler = new ExportJobItemsQueryHandler(repository, snapshots);

        var result = await handler.HandleAsync(new ExportJobItemsQuery(jobId));

        await transaction.DidNotReceive().CompleteAsync(Arg.Any<CancellationToken>());
        await using var export = result.Value;
        await foreach (var _ in export.ReadAsync())
        { }
        await export.CompleteAsync();
        await snapshots.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }
}
