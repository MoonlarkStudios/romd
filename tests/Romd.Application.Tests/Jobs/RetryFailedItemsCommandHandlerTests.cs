using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public class RetryFailedItemsCommandHandlerTests
{
    private readonly IJobRepository _jobRepo = Substitute.For<IJobRepository>();
    private readonly IEnrichmentScheduler _scheduler = Substitute.For<IEnrichmentScheduler>();

    private RetryFailedItemsCommandHandler CreateHandler() => new(_jobRepo, _scheduler);

    private static BulkEnrichmentJob CreateBulkJobWithFailures(int platformId = 5)
    {
        var job = BulkEnrichmentJob.Create(platformId, "SNES");
        job.Start("hangfire-1");
        job.SetTotalTitles(3);
        job.RecordEnriched();
        job.RecordFailed(101, "Chrono Trigger", "All providers failed", JobErrorReason.ProviderError);
        job.RecordFailed(102, "Final Fantasy VI", "boom", JobErrorReason.ProcessingError);
        return job;
    }

    [Fact]
    public async Task HandleAsync_ReEnqueuesFailedTitles_AndReturnsCount()
    {
        var job = CreateBulkJobWithFailures();
        _jobRepo.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await CreateHandler().HandleAsync(new RetryFailedItemsCommand(job.Id));

        result.IsError.ShouldBeFalse();
        result.Value.Requeued.ShouldBe(2);
        await _scheduler.Received(1).EnqueueBatchAsync(
            Arg.Is<IEnumerable<(int TitleId, string TitleName, int PlatformId)>>(titles =>
                titles.Count() == 2 &&
                titles.Any(t => t.TitleId == 101 && t.PlatformId == 5) &&
                titles.Any(t => t.TitleId == 102)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReturnsNotFound_WhenJobMissing()
    {
        var missingId = Guid.NewGuid();
        _jobRepo.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Job?)null);

        var result = await CreateHandler().HandleAsync(new RetryFailedItemsCommand(missingId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
        await _scheduler.DidNotReceiveWithAnyArgs().EnqueueBatchAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNotRetryable_WhenNoEntityFailures()
    {
        var job = BulkEnrichmentJob.Create(5, "SNES");
        job.Start("hangfire-1");
        job.SetTotalTitles(1);
        job.RecordEnriched();
        _jobRepo.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await CreateHandler().HandleAsync(new RetryFailedItemsCommand(job.Id));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        await _scheduler.DidNotReceiveWithAnyArgs().EnqueueBatchAsync(default!, default);
    }
}
