using Romd.Infrastructure.Tests.Helpers;
using System.Data.Common;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class DurableJobDispatchTests
{
    public static TheoryData<string> Families => new() { "upload", "path_import", "replace_dat", "export", "enrichment", "bulk_enrichment", "materialization" };

    public static TheoryData<string, bool> CheckpointCases => new()
    {
        { "upload", false }, { "upload", true },
        { "replace_dat", false }, { "replace_dat", true },
        { "export", false }, { "export", true },
        { "enrichment", false }, { "enrichment", true },
        { "bulk_enrichment", false }, { "bulk_enrichment", true },
        { "materialization", false }, { "materialization", true }
    };

    [Theory]
    [MemberData(nameof(CheckpointCases))]
    public async Task Checkpoint_StagingRequiresOwner_OnlyCallerFlushAndCommitPersistState(string family, bool generic)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var seed = database.CreateContext();
        var job = await SeedAsync(seed, family);
        seed.Add(ToEntity(job));
        await seed.SaveChangesAsync();
        await (job switch
        {
            UploadJob upload => StageCheckpointAsync(database, upload, generic, context => new UploadJobRepository(context, TimeProvider.System)),
            ReplaceDatJob replace => StageCheckpointAsync(database, replace, generic, context => new ReplaceDatJobRepository(context, TimeProvider.System)),
            ExportJob export => StageCheckpointAsync(database, export, generic, context => new ExportJobRepository(context, TimeProvider.System)),
            EnrichmentJob enrichment => StageCheckpointAsync(database, enrichment, generic, context => new EnrichmentJobRepository(context, TimeProvider.System)),
            BulkEnrichmentJob bulk => StageCheckpointAsync(database, bulk, generic, context => new BulkEnrichmentJobRepository(context, TimeProvider.System)),
            MaterializationJob materialization => StageCheckpointAsync(database, materialization, generic, context => new MaterializationJobRepository(context, TimeProvider.System)),
            _ => throw new InvalidOperationException()
        });
    }

    private static async Task StageCheckpointAsync<TJob, TRepository>(PostgreSqlTestDatabase database, TJob job,
        bool generic, Func<RomdDbContext, TRepository> createRepository) where TJob : Job where TRepository : IJobRepository<TJob>
    {
        await using var context = database.CreateContext();
        await using var observer = database.CreateContext();
        var typed = createRepository(context);
        var allJobs = new JobRepository(context, TimeProvider.System);
        Task Stage(CancellationToken ct = default) => generic
            ? allJobs.UpdateStagedAsync(job, ct)
            : typed.UpdateStagedAsync(job, ct);
        job.SetCurrentItem("staged checkpoint");
        await Should.ThrowAsync<InvalidOperationException>(() => Stage());
        context.ChangeTracker.Entries().ShouldBeEmpty();
        (await observer.Jobs.SingleAsync()).CurrentItem.ShouldBeNull();

        var unitOfWork = new EfUnitOfWork(context);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        (await Should.ThrowAsync<OperationCanceledException>(() => Stage(cancelled.Token)))
            .CancellationToken.ShouldBe(cancelled.Token);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        await Stage();
        (await context.Jobs.Select(row => row.CurrentItem).SingleAsync()).ShouldBeNull();
        await unitOfWork.FlushAsync();
        (await context.Jobs.Select(row => row.CurrentItem).SingleAsync()).ShouldBe("staged checkpoint");
        (await observer.Jobs.SingleAsync()).CurrentItem.ShouldBeNull();
        await transaction.CommitAsync();
        (await observer.Jobs.SingleAsync()).CurrentItem.ShouldBe("staged checkpoint");
    }

    [Theory]
    [MemberData(nameof(CheckpointCases))]
    public async Task Checkpoint_FailureAfterPhaseFlush_RollsBackFenceAndAllowsSameContextRetry(string family, bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var seed = database.CreateContext();
        var job = await SeedAsync(seed, family);
        seed.Add(ToEntity(job));
        await seed.SaveChangesAsync();
        await (job switch
        {
            UploadJob upload => FailCheckpointAsync(database, upload, cancel, context => new UploadJobRepository(context, TimeProvider.System)),
            ReplaceDatJob replace => FailCheckpointAsync(database, replace, cancel, context => new ReplaceDatJobRepository(context, TimeProvider.System)),
            ExportJob export => FailCheckpointAsync(database, export, cancel, context => new ExportJobRepository(context, TimeProvider.System)),
            EnrichmentJob enrichment => FailCheckpointAsync(database, enrichment, cancel, context => new EnrichmentJobRepository(context, TimeProvider.System)),
            BulkEnrichmentJob bulk => FailCheckpointAsync(database, bulk, cancel, context => new BulkEnrichmentJobRepository(context, TimeProvider.System)),
            MaterializationJob materialization => FailCheckpointAsync(database, materialization, cancel, context => new MaterializationJobRepository(context, TimeProvider.System)),
            _ => throw new InvalidOperationException()
        });
    }

    private static async Task FailCheckpointAsync<TJob, TRepository>(PostgreSqlTestDatabase database, TJob job,
        bool cancel, Func<RomdDbContext, TRepository> createRepository) where TJob : CancellableJob where TRepository : IJobRepository<TJob>
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var observer = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        JobEntity? before = null;
        var fault = new AfterCheckpointFlush(async () =>
        {
            var visible = await observer.Jobs.SingleAsync();
            visible.Phase.ShouldBe(before!.Phase);
            visible.CurrentItem.ShouldBe(before.CurrentItem);
            visible.ExecutionFenceToken.ShouldBe(before.ExecutionFenceToken);
            visible.ExecutionLeaseExpiresAtUtc.ShouldBe(before.ExecutionLeaseExpiresAtUtc);
            visible.ExecutionFenceToken.ShouldNotBeNull();
        }, cancel ? cancellation : null);
        await using var context = new RomdDbContext(database.CreateOptions(builder => builder.AddInterceptors(fault)));
        var repository = new ClaimedJobRepository<TJob, TRepository>(createRepository(context), context, clock, new());
        var claim = (await repository.TryClaimExecutionAsync(job.Id, "delivery"))!;
        before = await observer.Jobs.SingleAsync();
        claim.Job.SetCurrentItem("uncommitted checkpoint");
        fault.Enabled = true;

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => repository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await Should.ThrowAsync<InvalidOperationException>(() => repository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)))
                .ShouldBeSameAs(fault.Failure);

        fault.Reached.ShouldBeTrue();
        context.Database.CurrentTransaction.ShouldBeNull();
        context.ChangeTracker.Entries().ShouldBeEmpty();
        var rolledBack = await observer.Jobs.SingleAsync();
        rolledBack.Phase.ShouldBe(before.Phase);
        rolledBack.CurrentItem.ShouldBe(before.CurrentItem);
        rolledBack.ExecutionFenceToken.ShouldBe(before.ExecutionFenceToken);
        rolledBack.ExecutionLeaseExpiresAtUtc.ShouldBe(before.ExecutionLeaseExpiresAtUtc);

        fault.Enabled = false;
        claim.Job.SetCurrentItem("retry checkpoint");
        (await repository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();
        var retried = await observer.Jobs.SingleAsync();
        retried.CurrentItem.ShouldBe("retry checkpoint");
        retried.ExecutionFenceToken.ShouldBe(claim.FenceToken);
        claim.Job.Cancel(clock.GetUtcNow());
        (await repository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeTrue();
        var terminal = await observer.Jobs.SingleAsync();
        terminal.Phase.ShouldBe("Cancelled");
        terminal.ExecutionFenceToken.ShouldBeNull();
        terminal.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
        (await repository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeFalse();
    }

    private sealed class AfterCheckpointFlush(Func<Task> observe, CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Reached { get; private set; }
        public InvalidOperationException Failure { get; } = new("Injected failure after checkpoint flush.");

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Reached = true;
                await observe();
                if (cancellation is null)
                    throw Failure;
                cancellation.Cancel();
            }
            return result;
        }
    }

    [Theory]
    [InlineData("enrichment")]
    [InlineData("bulk_enrichment")]
    [InlineData("export")]
    public async Task Acceptance_ConcurrentDedupLookups_CommitOnlyOneJobAndDispatch(string family)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using (var seed = database.CreateContext())
            await SeedAsync(seed, family);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        async Task AcceptAsync()
        {
            await using var context = database.CreateContext();
            await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
            if (Interlocked.Increment(ref started) == 2)
                ready.SetResult();
            await ready.Task;
            bool pending = family switch
            {
                "enrichment" => await new EnrichmentJobRepository(context, TimeProvider.System).LockAndHasPendingForTitleStagedAsync(1),
                "bulk_enrichment" => await new BulkEnrichmentJobRepository(context, TimeProvider.System).LockAndGetPendingForPlatformStagedAsync(1) is not null,
                _ => await new ExportJobRepository(context, TimeProvider.System).LockAndHasPendingStagedAsync()
            };
            if (pending)
                return;
            Job job = family switch
            {
                "enrichment" => EnrichmentJob.Create("Test", 1, 1, TimeProvider.System.GetUtcNow()),
                "bulk_enrichment" => BulkEnrichmentJob.Create(1, "test", createdAt: DateTimeOffset.UtcNow),
                _ => ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, null)
            };
            context.Add(ToEntity(job));
            await transaction.CommitAsync();
        }
        await Task.WhenAll(AcceptAsync(), AcceptAsync());
        await using var assert = database.CreateContext();
        (await assert.Jobs.CountAsync()).ShouldBe(1);
        (await assert.JobDispatches.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Acceptance_RollbackOrCancellation_PublishesNeitherJobNorDispatch(string family)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var job = await SeedAsync(context, family);
        await using (var transaction = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            context.Add(ToEntity(job));
            await context.SaveChangesAsync();
            await using var observer = database.CreateContext();
            (await observer.Jobs.CountAsync()).ShouldBe(0);
            (await observer.JobDispatches.CountAsync()).ShouldBe(0);
            await transaction.RollbackAsync();
        }
        context.Add(ToEntity(job));
        await Should.ThrowAsync<OperationCanceledException>(() => context.SaveChangesAsync(new CancellationToken(true)));
        await using var after = database.CreateContext();
        (await after.Jobs.CountAsync()).ShouldBe(0);
        (await after.JobDispatches.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Dispatch_CrashAfterAcceptanceAndAfterVisibility_ConvergesOnOneStableJob(string family)
    {
        using var database = PostgreSqlTestDatabase.Create();
        Guid jobId;
        await using (var acceptance = database.CreateContext())
        {
            var job = await SeedAsync(acceptance, family);
            jobId = job.Id;
            acceptance.Add(ToEntity(job));
            await acceptance.SaveChangesAsync();
        }
        // The accepting scope is gone before a transport exists, modelling a post-commit crash.
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var failure = new FailAcknowledgementOnce();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(failure).Options;
        await using var dispatchContext = new RomdDbContext(options);
        var transport = Substitute.For<IBackgroundJobClient>();
        transport.Create(Arg.Any<Hangfire.Common.Job>(), Arg.Any<IState>()).Returns("delivery-a", "delivery-b");
        var repository = new JobDispatchRepository(dispatchContext, clock);
        var service = new JobDispatchService(repository, transport, NullLogger<JobDispatchService>.Instance);

        await service.DispatchAsync(jobId);
        (await dispatchContext.JobDispatches.SingleAsync()).DeliveredAtUtc.ShouldBeNull();
        (await dispatchContext.JobDispatches.SingleAsync()).LastError.ShouldNotBeNull().ShouldContain("Injected acknowledgement crash");
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.DispatchAsync(jobId);
        await service.DispatchAsync(jobId);

        var dispatch = await dispatchContext.JobDispatches.SingleAsync();
        dispatch.JobId.ShouldBe(jobId);
        dispatch.AttemptCount.ShouldBe(2);
        dispatch.DeliveredAtUtc.ShouldNotBeNull();
        dispatch.HangfireJobId.ShouldBe("delivery-b");
        (await dispatchContext.Jobs.SingleAsync()).Id.ShouldBe(jobId);
        transport.Received(2).Create(
            Arg.Is<Hangfire.Common.Job>(job => (Guid)job.Args[0] == jobId), Arg.Any<IState>());
        var (invocation, queue) = JobDispatchService.CreateInvocation(dispatch.JobType, jobId);
        invocation.Method.GetCustomAttributes(typeof(QueueAttribute), false)
            .Cast<QueueAttribute>().Single().Queue.ShouldBe(queue);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Execution_CompetingDeliveriesCrashHandoffAndCancellation_RejectStaleWrites(string family)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var seed = database.CreateContext();
        var job = await SeedAsync(seed, family);
        seed.Add(ToEntity(job));
        await seed.SaveChangesAsync();
        await (job switch
        {
            UploadJob upload => ExerciseAsync(database, upload, context => new UploadJobRepository(context, TimeProvider.System)),
            ReplaceDatJob replace => ExerciseAsync(database, replace, context => new ReplaceDatJobRepository(context, TimeProvider.System)),
            ExportJob export => ExerciseAsync(database, export, context => new ExportJobRepository(context, TimeProvider.System)),
            EnrichmentJob enrichment => ExerciseAsync(database, enrichment, context => new EnrichmentJobRepository(context, TimeProvider.System)),
            BulkEnrichmentJob bulk => ExerciseAsync(database, bulk, context => new BulkEnrichmentJobRepository(context, TimeProvider.System)),
            MaterializationJob materialization => ExerciseAsync(database, materialization, context => new MaterializationJobRepository(context, TimeProvider.System)),
            _ => throw new InvalidOperationException()
        });
    }

    private static async Task ExerciseAsync<TJob, TRepository>(PostgreSqlTestDatabase database, TJob job,
        Func<RomdDbContext, TRepository> createRepository) where TJob : CancellableJob where TRepository : IJobRepository<TJob>
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var options = new JobExecutionClaimOptions();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = new ClaimedJobRepository<TJob, TRepository>(createRepository(firstContext), firstContext, clock, options);
        var second = new ClaimedJobRepository<TJob, TRepository>(createRepository(secondContext), secondContext, clock, options);
        var claims = await Task.WhenAll(first.TryClaimExecutionAsync(job.Id, "delivery-a"), second.TryClaimExecutionAsync(job.Id, "delivery-b"));
        var original = claims.OfType<JobExecutionClaim<TJob>>().ShouldHaveSingleItem();
        // A shutdown redelivery can encounter a still-live lease and do no work. The shared
        // dispatch record must recover that abandoned execution after expiry, not depend on
        // another Hangfire retry happening to arrive later.
        var dispatches = new JobDispatchRepository(firstContext, clock);
        var dispatch = (await dispatches.TryClaimAsync(job.Id, default)).ShouldNotBeNull();
        await dispatches.AcknowledgeAsync(dispatch, "delivery-a", default);
        (await dispatches.GetAvailableAsync(25, default)).ShouldBeEmpty();
        clock.Advance(options.LeaseDuration);
        (await dispatches.GetAvailableAsync(25, default)).ShouldContain(job.Id);
        (await dispatches.TryClaimAsync(job.Id, default)).ShouldNotBeNull();
        var replacement = (await second.TryClaimExecutionAsync(job.Id, "delivery-c")).ShouldNotBeNull();
        replacement.FenceToken.ShouldNotBe(original.FenceToken);
        (await first.TryUpdateClaimedAsync(original.Job, original.FenceToken)).ShouldBeFalse();

        // Child command scopes inherit the fence and must reject stale application mutations.
        using (JobExecutionFenceScope.Enter(job.Id, original.FenceToken, clock))
        {
            await using var mutationContext = database.CreateContext();
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => new EfUnitOfWork(mutationContext).BeginTransactionAsync());
            var item = JobItem.ForRom(job.Id, "stale.rom", 1, RomIngestOutcome.Ingested,
                null, null, null, null, createdAt: DateTimeOffset.UtcNow);
            await using var unowned = await mutationContext.Database.BeginTransactionAsync();
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() =>
                new JobItemRepository(mutationContext).AddRangeStagedAsync([item]));
            (await mutationContext.JobItems.CountAsync()).ShouldBe(0);
        }

        replacement.Job.Cancel(cancelledAt: DateTimeOffset.UtcNow);
        await using (var cancellationContext = database.CreateContext())
            await new JobRepository(cancellationContext, clock).CommitCheckpointAsync(cancellationContext, replacement.Job);
        (await second.TryUpdateClaimedAsync(original.Job, replacement.FenceToken)).ShouldBeFalse();
        (await second.TryClaimExecutionAsync(job.Id, "delivery-d")).ShouldBeNull();
        (await second.GetByIdAsync(job.Id)).ShouldNotBeNull().Phase.ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Dispatch_ExpiredClaimAndOnePoisonMessage_DoNotBlockOtherJobs()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var broken = UploadJob.Create("broken.zip", createdAt: DateTimeOffset.UtcNow);
        var healthy = UploadJob.Create("healthy.zip", createdAt: DateTimeOffset.UtcNow);
        context.AddRange(ToEntity(broken), ToEntity(healthy));
        await context.SaveChangesAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var repository = new JobDispatchRepository(context, clock);
        (await repository.TryClaimAsync(broken.Id, default)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(1));
        var transport = Substitute.For<IBackgroundJobClient>();
        transport.Create(Arg.Any<Hangfire.Common.Job>(), Arg.Any<IState>()).Returns(call =>
            (Guid)call.Arg<Hangfire.Common.Job>().Args[0] == broken.Id
                ? throw new InvalidOperationException("transport unavailable for one message") : "healthy-delivery");
        var service = new JobDispatchService(repository, transport, NullLogger<JobDispatchService>.Instance);
        foreach (var id in await repository.GetAvailableAsync(25, default))
            await service.DispatchAsync(id);
        (await context.JobDispatches.SingleAsync(row => row.JobId == broken.Id)).LastError.ShouldNotBeNull();
        (await context.JobDispatches.SingleAsync(row => row.JobId == healthy.Id)).DeliveredAtUtc.ShouldNotBeNull();
    }

    private static async Task<Job> SeedAsync(RomdDbContext context, string family)
    {
        context.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = DateTimeOffset.UtcNow });
        context.Titles.Add(new TitleEntity { Id = 1, PlatformId = 1, Name = "Test", NormalizedName = "test", EnrichmentStatus = "None", CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        return family switch
        {
            "upload" => UploadJob.Create("upload.zip", createdAt: DateTimeOffset.UtcNow),
            "path_import" => CreatePathImport(),
            "replace_dat" => ReplaceDatJob.Create(1, "replacement.dat", createdAt: DateTimeOffset.UtcNow, 1),
            "export" => ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, null),
            "enrichment" => EnrichmentJob.Create("Test", 1, 1, TimeProvider.System.GetUtcNow()),
            "bulk_enrichment" => BulkEnrichmentJob.Create(1, "test", createdAt: DateTimeOffset.UtcNow),
            "materialization" => MaterializationJob.Create(1, "Test", createdAt: DateTimeOffset.UtcNow),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private static UploadJob CreatePathImport()
    {
        var job = UploadJob.Create("import/", createdAt: DateTimeOffset.UtcNow);
        job.SetImportSource("/test/source", true);
        return job;
    }

    private static JobEntity ToEntity(Job job) => job switch
    {
        UploadJob upload => UploadJobEntity.FromDomain(upload),
        ReplaceDatJob replace => ReplaceDatJobEntity.FromDomain(replace),
        ExportJob export => ExportJobEntity.FromDomain(export),
        EnrichmentJob enrichment => EnrichmentJobEntity.FromDomain(enrichment),
        BulkEnrichmentJob bulk => BulkEnrichmentJobEntity.FromDomain(bulk),
        MaterializationJob materialization => MaterializationJobEntity.FromDomain(materialization),
        _ => throw new InvalidOperationException()
    };

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

    private sealed class FailAcknowledgementOnce : DbCommandInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed && command.CommandText.Contains("UPDATE romd.\"JobDispatches\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"DeliveredAtUtc\" =", StringComparison.Ordinal)
                && command.CommandText.Contains("\"HangfireJobId\" =", StringComparison.Ordinal))
            {
                _failed = true;
                throw new InvalidOperationException("Injected acknowledgement crash");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
