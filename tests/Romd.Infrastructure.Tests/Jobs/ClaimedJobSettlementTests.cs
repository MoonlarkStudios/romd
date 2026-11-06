using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ClaimedJobSettlementTests
{
    [Theory]
    [InlineData("claim", "none", false)]
    [InlineData("checkpoint", "none", false)]
    [InlineData("defer", "none", false)]
    [InlineData("claim", "before", false)]
    [InlineData("checkpoint", "before", false)]
    [InlineData("defer", "before", false)]
    [InlineData("claim", "after", false)]
    [InlineData("checkpoint", "after", false)]
    [InlineData("defer", "after", false)]
    [InlineData("claim", "before", true)]
    [InlineData("checkpoint", "before", true)]
    [InlineData("defer", "before", true)]
    [InlineData("claim", "after", true)]
    [InlineData("checkpoint", "after", true)]
    [InlineData("defer", "after", true)]
    public async Task Protocol_ProviderSettlementAndCleanup_PreservesAcknowledgementAndPrimary(string operation, string fault, bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var job = UploadJob.Create("settlement.zip", DateTimeOffset.UtcNow);
        JobExecutionClaim<UploadJob>? claim = null;
        var due = DateTimeOffset.UnixEpoch.AddDays(30000);
        await using (var seed = database.CreateContext())
        {
            await using (var acceptance = await new EfUnitOfWork(seed).BeginTransactionAsync())
            {
                await new UploadJobRepository(seed, TimeProvider.System).AddStagedAsync(job);
                seed.JobDispatches.Add(new JobDispatchEntity
                {
                    JobId = job.Id,
                    JobType = "Upload",
                    CreatedAtUtc = job.CreatedAt,
                    AvailableAtUtc = job.CreatedAt,
                    DeliveredAtUtc = job.CreatedAt,
                    HangfireJobId = "initial-delivery"
                });
                await acceptance.CommitAsync();
            }
            seed.ChangeTracker.Clear();
            if (operation != "claim")
            {
                claim = await Claims(seed).TryClaimExecutionAsync(job.Id, "initial-delivery");
                job = claim.ShouldNotBeNull().Job;
                if (operation == "checkpoint")
                {
                    job.BeginClassification();
                    job.SetDiscoveredFiles(0, 0);
                    job.BeginDatIngestion();
                    job.BeginRomIngestion();
                    job.Complete(DateTimeOffset.UtcNow);
                }
                else
                    job.RecordDeferral("capacity", DateTimeOffset.UtcNow);
            }
        }
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel
            ? new OperationCanceledException("Interrupted provider acknowledgement", cancellation.Token)
            : new IOException("Provider acknowledgement failed");
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        var faults = new ProviderTransactionFaults(fault, primary, cancel ? cancellation.Cancel : null);
        faults.Register(db);
        var logger = Substitute.For<ILogger<ClaimedJobRepository<UploadJob, UploadJobRepository>>>();
        var repository = Claims(db, logger);
        bool acknowledged = false;

        var actual = await Record.ExceptionAsync(async () =>
        {
            acknowledged = operation switch
            {
                "claim" => await repository.TryClaimExecutionAsync(job.Id, "new-delivery", cancellation.Token) is not null,
                "checkpoint" => await repository.TryUpdateClaimedAsync(job, claim!.FenceToken, cancellation.Token),
                _ => await repository.TryDeferClaimedAsync(job, claim!.FenceToken, due, cancellation.Token)
            };
        });

        acknowledged.ShouldBe(fault == "none");
        if (fault == "none")
            actual.ShouldBeNull();
        else
        {
            actual.ShouldBeSameAs(primary);
            actual!.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
            if (cancel)
                ((OperationCanceledException)actual).CancellationToken.ShouldBe(cancellation.Token);
        }
        faults.Disposals.ShouldBe(1);
        faults.Commits.ShouldBe(fault == "before" ? 0 : 1);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log" &&
            ReferenceEquals(call.GetArguments()[3], faults.CleanupFailure));
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var verify = database.CreateContext();
        var stored = await verify.Jobs.SingleAsync();
        bool durable = fault != "before";
        stored.Phase.ShouldBe(operation switch
        {
            "claim" => durable ? nameof(UploadPhase.Extracting) : nameof(UploadPhase.Pending),
            "checkpoint" => durable ? nameof(UploadPhase.Completed) : nameof(UploadPhase.Extracting),
            _ => nameof(UploadPhase.Extracting)
        });
        if (operation == "claim")
            stored.ExecutionFenceToken.HasValue.ShouldBe(durable);
        else
            stored.ExecutionFenceToken.HasValue.ShouldBe(!durable);
        if (operation == "defer")
        {
            stored.NextAttemptAtUtc.ShouldBe(durable ? due : null);
            var dispatch = await verify.JobDispatches.SingleAsync();
            dispatch.DeliveredAtUtc.HasValue.ShouldBe(!durable);
            if (durable)
            {
                dispatch.AvailableAtUtc.ShouldBe(due);
                stored.HangfireJobId.ShouldBeNull();
            }
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        (await verify.Jobs.CountAsync()).ShouldBe(1);
    }

    private static ClaimedJobRepository<UploadJob, UploadJobRepository> Claims(RomdDbContext db,
        ILogger<ClaimedJobRepository<UploadJob, UploadJobRepository>>? logger = null) =>
        new(new UploadJobRepository(db, TimeProvider.System), db, TimeProvider.System, new JobExecutionClaimOptions(), logger);
}
