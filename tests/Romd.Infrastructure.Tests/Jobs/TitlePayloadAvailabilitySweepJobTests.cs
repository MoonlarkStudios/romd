using Hangfire;
using NSubstitute;
using Romd.Application.Common.Jobs;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class TitlePayloadAvailabilitySweepJobTests
{
    [Fact]
    public async Task ExecuteAsync_MultiplePages_CommitsEachBoundedPageInKeysetOrder()
    {
        var projection = Substitute.For<ITitlePayloadAvailabilityProjection>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var firstTransaction = Substitute.For<ITransaction>();
        var secondTransaction = Substitute.For<ITransaction>();
        var events = new List<string>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(firstTransaction, secondTransaction);
        projection.AuditBatchAsync(null, TitlePayloadAvailabilitySweepJob.BatchSize, Arg.Any<CancellationToken>())
            .Returns(new PayloadAvailabilityAuditResult(5_000, 40));
        projection.AuditBatchAsync(40, TitlePayloadAvailabilitySweepJob.BatchSize, Arg.Any<CancellationToken>())
            .Returns(new PayloadAvailabilityAuditResult(20, null));
        firstTransaction.When(item => item.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => events.Add("commit:first"));
        secondTransaction.When(item => item.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => events.Add("commit:second"));

        await new TitlePayloadAvailabilitySweepJob(projection, unitOfWork)
            .ExecuteAsync(CancellationToken.None);

        events.ShouldBe(["commit:first", "commit:second"]);
        Received.InOrder(() =>
        {
            projection.AuditBatchAsync(null, TitlePayloadAvailabilitySweepJob.BatchSize,
                Arg.Any<CancellationToken>());
            firstTransaction.CommitAsync(Arg.Any<CancellationToken>());
            projection.AuditBatchAsync(40, TitlePayloadAvailabilitySweepJob.BatchSize,
                Arg.Any<CancellationToken>());
            secondTransaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_PageAfterCommittedPageFails_LaterRootReauditsCommittedPageAndConverges()
    {
        var projection = Substitute.For<ITitlePayloadAvailabilityProjection>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transactions = Enumerable.Range(0, 4)
            .Select(_ => Substitute.For<ITransaction>())
            .ToArray();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(transactions[0], transactions[1], transactions[2], transactions[3]);
        projection.AuditBatchAsync(null, TitlePayloadAvailabilitySweepJob.BatchSize,
                Arg.Any<CancellationToken>())
            .Returns(
                new PayloadAvailabilityAuditResult(5_000, 40),
                new PayloadAvailabilityAuditResult(5_000, 40));
        int secondPageAttempts = 0;
        projection.AuditBatchAsync(40, TitlePayloadAvailabilitySweepJob.BatchSize,
                Arg.Any<CancellationToken>())
            .Returns(_ => ++secondPageAttempts == 1
                ? Task.FromException<PayloadAvailabilityAuditResult>(
                    new InvalidOperationException("repair failed"))
                : Task.FromResult(new PayloadAvailabilityAuditResult(20, null)));
        var job = new TitlePayloadAvailabilitySweepJob(projection, unitOfWork);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            job.ExecuteAsync(CancellationToken.None));
        await job.ExecuteAsync(CancellationToken.None);

        await transactions[0].Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await transactions[1].DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transactions[2].Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await transactions[3].Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await projection.Received(2).AuditBatchAsync(
            null,
            TitlePayloadAvailabilitySweepJob.BatchSize,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExecuteAsync_HangfireAdmission_RejectsOverlappingRootsWithoutRetryAmplification()
    {
        var method = typeof(TitlePayloadAvailabilitySweepJob)
            .GetMethod(nameof(TitlePayloadAvailabilitySweepJob.ExecuteAsync))!;

        var admission = method.GetCustomAttributes(inherit: true)
            .OfType<DisableConcurrentExecutionAttribute>()
            .ShouldHaveSingleItem();
        admission.TimeoutSec.ShouldBe(0);
        method.GetCustomAttributes(inherit: true)
            .OfType<AutomaticRetryAttribute>()
            .ShouldHaveSingleItem()
            .Attempts.ShouldBe(0);
        method.GetCustomAttributes(inherit: true)
            .OfType<QueueAttribute>()
            .ShouldHaveSingleItem()
            .Queue.ShouldBe(JobQueues.Default);
    }
}
