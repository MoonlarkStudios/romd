using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
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
        var jobId = Guid.NewGuid();
        var expected = new JobItemPageResult([], false);
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetByJobAsync(jobId, null, 25, Arg.Any<CancellationToken>()).Returns(expected);
        var handler = new GetJobItemsQueryHandler(repository, snapshots);

        var result = await handler.HandleAsync(new GetJobItemsQuery(jobId, null, 25));

        result.Value.ShouldBeSameAs(expected);
        await snapshots.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportJobItems_HandleAsync_OwnsAndCommitsReadSnapshot()
    {
        var repository = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var transaction = Substitute.For<IReadSnapshotTransaction>();
        var jobId = Guid.NewGuid();
        IReadOnlyList<JobItemView> expected = [];
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetAllByJobAsync(jobId, Arg.Any<CancellationToken>()).Returns(expected);
        var handler = new ExportJobItemsQueryHandler(repository, snapshots);

        var result = await handler.HandleAsync(new ExportJobItemsQuery(jobId));

        result.Value.ShouldBeSameAs(expected);
        await snapshots.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
    }
}
