using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class RetryFailedItemsCommandHandlerTests
{
    private readonly IJobRepository jobRepo = Substitute.For<IJobRepository>();
    private readonly ICommandHandler<QueueTitleEnrichmentJobCommand, Success> acceptance = Substitute.For<ICommandHandler<QueueTitleEnrichmentJobCommand, Success>>();

    private RetryFailedItemsCommandHandler Handler() => new(jobRepo, acceptance);

    private static BulkEnrichmentJob FailedBulk(int platformId = 5)
    {
        var job = BulkEnrichmentJob.Create(platformId, "SNES", DateTimeOffset.UnixEpoch);
        job.Start("hangfire-1", DateTimeOffset.UnixEpoch);
        job.SetTotalTitles(5);
        job.RecordEnriched();
        job.RecordFailed(101, "Chrono Trigger", "All providers failed", JobErrorReason.ProviderError, DateTimeOffset.UnixEpoch);
        job.RecordFailed(102, "Final Fantasy VI", "boom", JobErrorReason.ProcessingError, DateTimeOffset.UnixEpoch);
        return job;
    }

    [Fact]
    public async Task Handle_RequeuesCapturedUniqueFailureFacts_AndCountsSelectedTitles()
    {
        var job = FailedBulk();
        job.RecordFailed(101, "Later duplicate name", "again", JobErrorReason.ProviderError, DateTimeOffset.UnixEpoch);
        job.RecordError("job", "Non-title failure", DateTimeOffset.UnixEpoch);
        var original = job.Errors.ToArray();
        jobRepo.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var requested = new List<QueueTitleEnrichmentJobCommand>();
        acceptance.HandleAsync(Arg.Any<QueueTitleEnrichmentJobCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            requested.Add(call.Arg<QueueTitleEnrichmentJobCommand>());
            // Successful coalescing and successful creation deliberately share this result.
            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        });
        using var cancellation = new CancellationTokenSource();

        var result = await Handler().HandleAsync(new(job.Id), cancellation.Token);

        result.Value.Requeued.ShouldBe(2);
        requested.ShouldBe([new(101, "Chrono Trigger", 5), new(102, "Final Fantasy VI", 5)]);
        job.Errors.ShouldBe(original);
        await acceptance.Received(1).HandleAsync(requested[0], cancellation.Token);
        await acceptance.Received(1).HandleAsync(requested[1], cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_LaterAcceptanceFails_PreservesCauseAndDoesNotAttemptRemainingTitles(bool cancel)
    {
        var job = FailedBulk();
        job.RecordFailed(103, "Third title", "failed", JobErrorReason.ProviderError, DateTimeOffset.UnixEpoch);
        jobRepo.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var attempted = new List<int>();
        Exception cause = cancel ? new OperationCanceledException() : new IOException("second acceptance failed");
        acceptance.HandleAsync(Arg.Any<QueueTitleEnrichmentJobCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            attempted.Add(call.Arg<QueueTitleEnrichmentJobCommand>().TitleId);
            return attempted.Count == 2 ? Task.FromException<ErrorOr<Success>>(cause) : Task.FromResult<ErrorOr<Success>>(Result.Success);
        });

        var error = await CaptureAsync(() => Handler().HandleAsync(new(job.Id)));

        error.ShouldBeSameAs(cause);
        attempted.ShouldBe([101, 102]);
        job.Errors.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Handle_JobMissing_ReturnsNotFoundWithoutAcceptance()
    {
        var missingId = Guid.NewGuid();
        jobRepo.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Job?)null);

        var result = await Handler().HandleAsync(new(missingId));

        result.FirstError.ShouldBe(RetryFailedItemsErrors.JobNotFound(missingId));
        acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_NoEntityFailures_ReturnsNotRetryableWithoutAcceptance()
    {
        var job = BulkEnrichmentJob.Create(5, "SNES", DateTimeOffset.UnixEpoch);
        jobRepo.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await Handler().HandleAsync(new(job.Id));

        result.FirstError.ShouldBe(RetryFailedItemsErrors.NotRetryable);
        acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }
}
