using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MetadataRematerializationRecoveryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    public void Dispose() => database.Dispose();
    private async Task<MetadataRematerializationJob> SeedAsync(bool live = false)
    {
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: clock.GetUtcNow());
        job.Start("delivery", startedAt: clock.GetUtcNow());
        job.SetTotalTitles(3);
        job.RecordProcessed(1, occurredAt: clock.GetUtcNow());
        var entity = MetadataRematerializationJobEntity.FromDomain(job);
        entity.ExecutionFenceToken = Guid.NewGuid();
        entity.ExecutionLeaseExpiresAtUtc = clock.GetUtcNow().AddMinutes(live ? 5 : -1);
        await using var db = database.CreateContext();
        db.Add(new PlatformEntity { Id = 1, Name = "System", ShortName = "system" });
        db.Add(entity);
        await db.SaveChangesAsync();
        // An accepted job's dispatch exists before Start; this fixture starts the row directly.
        db.JobDispatches.Add(new JobDispatchEntity
        {
            JobId = job.Id,
            JobType = MetadataRematerializationJobEntity.Type,
            CreatedAtUtc = clock.GetUtcNow(),
            AvailableAtUtc = clock.GetUtcNow(),
            DeliveredAtUtc = clock.GetUtcNow(),
            HangfireJobId = "delivery",
            ClaimToken = Guid.NewGuid(),
            LeaseExpiresAtUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        return job;
    }
    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new JobExecutionClaimOptions());
        services.AddScoped<MetadataRematerializationJobRepository>();
        services.AddScoped<IJobRepository<MetadataRematerializationJob>, ClaimedJobRepository<MetadataRematerializationJob, MetadataRematerializationJobRepository>>();
        services.AddScoped<JobAuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
        services.AddRomdJobLifecyclePolicies();
        return services.BuildServiceProvider();
    }
    private void Fail(ServiceProvider provider, Guid id, string delivery = "delivery") =>
        new HangfireJobStateSyncFilter(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<HangfireJobStateSyncFilter>.Instance)
            .OnStateApplied(FailedContext(id, delivery), Substitute.For<IWriteOnlyTransaction>());

    [Fact]
    public async Task Run_UnattributedCancellation_PreservesRequiredWorkAndRedeliveryCompletesIt()
    {
        var job = await SeedAsync();
        using var provider = Provider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException("Required work was interrupted.", cancellation.Token);
        var executor = Substitute.For<IResumableJobExecutor<MetadataRematerializationJob>>();
        int executions = 0;
        executor.ExecuteAsync(Arg.Any<MetadataRematerializationJob>(), Arg.Any<JobContext>()).Returns(call =>
        {
            if (++executions == 1)
                return Task.FromException(failure);
            var resumed = call.Arg<MetadataRematerializationJob>();
            resumed.CursorTitleId.ShouldBe(1);
            resumed.RecordProcessed(2, clock.GetUtcNow());
            resumed.RecordProcessed(3, clock.GetUtcNow());
            return Task.CompletedTask;
        });
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetTempPath());
        var runner = new JobRunner<MetadataRematerializationJob>(provider.GetRequiredService<IServiceScopeFactory>(), executor,
            Substitute.For<IJobNotifier>(), options, clock, new JobExecutionClaimOptions(), NullLogger<JobRunner<MetadataRematerializationJob>>.Instance);

        var observed = await Record.ExceptionAsync(() => runner.RunAsync(job.Id, "interrupted", CancellationToken.None));

        observed.ShouldBeSameAs(failure);
        observed.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        await using (var read = database.CreateContext())
        {
            var row = await read.Set<MetadataRematerializationJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Rematerializing");
            row.CompletedAt.ShouldBeNull();
            row.ProcessedCount.ShouldBe(1);
            row.CursorTitleId.ShouldBe(1);
            row.ToDomain().Errors.ShouldBeEmpty();
            row.ExecutionFenceToken.ShouldNotBeNull();
        }
        clock.Advance(TimeSpan.FromMinutes(6));

        await runner.RunAsync(job.Id, "resumed", CancellationToken.None);

        executions.ShouldBe(2);
        await using var settled = database.CreateContext();
        var done = await settled.Set<MetadataRematerializationJobEntity>().SingleAsync();
        done.Phase.ShouldBe("Completed");
        done.ProcessedCount.ShouldBe(3);
        done.CursorTitleId.ShouldBe(3);
        done.ToDomain().Errors.ShouldBeEmpty();
        done.ExecutionFenceToken.ShouldBeNull();
    }

    [Fact]
    public async Task ExhaustedDelivery_RearmsAtomically_ThenNewDeliveryResumesAndCompletesRemainingTitles()
    {
        var job = await SeedAsync();
        using var provider = Provider();
        Fail(provider, job.Id);
        await using (var db = database.CreateContext())
        {
            var row = await db.Set<MetadataRematerializationJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Rematerializing");
            row.CursorTitleId.ShouldBe(1);
            row.ProcessedCount.ShouldBe(1);
            row.ExecutionFenceToken.ShouldBeNull();
            row.HangfireJobId.ShouldBeNull();
            row.CompletedAt.ShouldBeNull();
            row.NextAttemptAtUtc.ShouldBe(clock.GetUtcNow().AddSeconds(30));
            var dispatch = await db.JobDispatches.SingleAsync();
            dispatch.DeliveredAtUtc.ShouldBeNull();
            dispatch.ClaimToken.ShouldBeNull();
            dispatch.LeaseExpiresAtUtc.ShouldBeNull();
            dispatch.HangfireJobId.ShouldBeNull();
            dispatch.AvailableAtUtc.ShouldBe(row.NextAttemptAtUtc.GetValueOrDefault());
            (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
            (await Claims(db).TryClaimExecutionAsync(job.Id, "early")).ShouldBeNull();
        }
        clock.Advance(TimeSpan.FromSeconds(30));
        var executor = Substitute.For<IResumableJobExecutor<MetadataRematerializationJob>>();
        executor.ExecuteAsync(Arg.Any<MetadataRematerializationJob>(), Arg.Any<JobContext>()).Returns(async call =>
        {
            var resumed = call.Arg<MetadataRematerializationJob>();
            resumed.CursorTitleId.ShouldBe(1);
            resumed.RecordProcessed(2, occurredAt: clock.GetUtcNow());
            await call.Arg<JobContext>().CheckpointAsync();
            resumed.RecordProcessed(3, occurredAt: clock.GetUtcNow());
        });
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetTempPath());
        await new JobRunner<MetadataRematerializationJob>(provider.GetRequiredService<IServiceScopeFactory>(), executor,
            Substitute.For<IJobNotifier>(), options, clock, new JobExecutionClaimOptions(), NullLogger<JobRunner<MetadataRematerializationJob>>.Instance)
            .RunAsync(job.Id, "next", CancellationToken.None);
        await using var assertion = database.CreateContext();
        var done = await assertion.Set<MetadataRematerializationJobEntity>().SingleAsync();
        done.Phase.ShouldBe("Completed");
        done.ProcessedCount.ShouldBe(3);
        done.CursorTitleId.ShouldBe(3);
        done.ExecutionFenceToken.ShouldBeNull();
    }
    private ClaimedJobRepository<MetadataRematerializationJob, MetadataRematerializationJobRepository> Claims(RomdDbContext db) =>
        new(new MetadataRematerializationJobRepository(db), db, clock, new());
    [Theory]
    [InlineData(true, "delivery")]
    [InlineData(false, "stale")]
    public async Task ExhaustedDelivery_LiveLeaseOrStaleDelivery_DoesNotStealOwnership(bool live, string delivery)
    {
        var job = await SeedAsync(live);
        using var provider = Provider();
        await using var db = database.CreateContext();
        var before = await db.Set<MetadataRematerializationJobEntity>().SingleAsync();
        Fail(provider, job.Id, delivery);
        var after = await db.Set<MetadataRematerializationJobEntity>().SingleAsync();
        after.ExecutionFenceToken.ShouldBe(before.ExecutionFenceToken);
        after.HangfireJobId.ShouldBe("delivery");
        after.NextAttemptAtUtc.ShouldBeNull();
        after.CursorTitleId.ShouldBe(1);
        (await db.JobDispatches.SingleAsync()).DeliveredAtUtc.ShouldNotBeNull();
        (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        if (live)
            (await Claims(db).TryClaimExecutionAsync(job.Id, "competing")).ShouldBeNull();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedDelivery_MissingDispatchOrDatabaseFailure_RollsBackRecovery(bool triggerFailure)
    {
        var job = await SeedAsync();
        using var provider = Provider();
        await using (var db = database.CreateContext())
        {
            if (!triggerFailure)
                await db.JobDispatches.ExecuteDeleteAsync();
            else
                await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION romd.fail_dispatch_reset() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected recovery failure'; END $$;
                CREATE TRIGGER fail_dispatch_reset BEFORE UPDATE ON romd."JobDispatches"
                    FOR EACH ROW EXECUTE FUNCTION romd.fail_dispatch_reset();
                """);
        }
        Fail(provider, job.Id);
        await using var assert = database.CreateContext();
        var row = await assert.Set<MetadataRematerializationJobEntity>().SingleAsync();
        row.ExecutionFenceToken.ShouldNotBeNull();
        row.HangfireJobId.ShouldBe("delivery");
        row.ExecutionLeaseExpiresAtUtc.GetValueOrDefault().ShouldBeLessThan(clock.GetUtcNow());
        row.NextAttemptAtUtc.ShouldBeNull();
        row.CursorTitleId.ShouldBe(1);
        (await assert.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        if (triggerFailure)
        {
            (await assert.JobDispatches.SingleAsync()).DeliveredAtUtc.ShouldNotBeNull();
            await assert.Database.ExecuteSqlRawAsync("""DROP TRIGGER fail_dispatch_reset ON romd."JobDispatches"; DROP FUNCTION romd.fail_dispatch_reset();""");
        }
        // The untouched expired claim can still be taken by the existing expiry/redelivery mechanism.
        (await Claims(assert).TryClaimExecutionAsync(job.Id, "expiry-redelivery")).ShouldNotBeNull();
    }
    [Fact]
    public async Task Runner_UnattributedCancellation_RethrowsAndPreservesRequiredWork()
    {
        var job = await SeedAsync();
        using var provider = Provider();
        var executor = Substitute.For<IResumableJobExecutor<MetadataRematerializationJob>>();
        executor.ExecuteAsync(Arg.Any<MetadataRematerializationJob>(), Arg.Any<JobContext>()).Returns(Task.FromException(new OperationCanceledException()));
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetTempPath());
        var runner = new JobRunner<MetadataRematerializationJob>(provider.GetRequiredService<IServiceScopeFactory>(), executor,
            Substitute.For<IJobNotifier>(), options, clock, new JobExecutionClaimOptions(), NullLogger<JobRunner<MetadataRematerializationJob>>.Instance);
        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, "retry", CancellationToken.None));
        await using var db = database.CreateContext();
        var row = await db.Set<MetadataRematerializationJobEntity>().SingleAsync();
        row.CompletedAt.ShouldBeNull();
        row.Phase.ShouldBe("Rematerializing");
        row.CursorTitleId.ShouldBe(1);
        row.ExecutionFenceToken.ShouldNotBeNull();
        row.ErrorsJson.ShouldBe("[]");
    }
    [Fact]
    public async Task ExhaustedDelivery_CompetingClaimCommitsWhileFilterWaits_RechecksNewLiveOwnership()
    {
        var job = await SeedAsync();
        using var provider = Provider();
        var gate = new ClaimGate();
        await using var claimDb = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(gate)));
        await claimDb.Database.OpenConnectionAsync();
        int pid = ((Npgsql.NpgsqlConnection)claimDb.Database.GetDbConnection()).ProcessID;
        var claimTask = Claims(claimDb).TryClaimExecutionAsync(job.Id, "competing-delivery");
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var filterTask = Task.Run(() => Fail(provider, job.Id));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var observer = database.CreateContext();
            while (!await observer.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE {pid} = ANY(pg_blocking_pids(pid))")
                       .AnyAsync(count => count > 0, timeout.Token))
                await Task.Yield();
            filterTask.IsCompleted.ShouldBeFalse();
        }
        finally { gate.Release.TrySetResult(); }
        var claimed = await claimTask.WaitAsync(TimeSpan.FromSeconds(10));
        claimed.ShouldNotBeNull();
        await filterTask.WaitAsync(TimeSpan.FromSeconds(10));
        await using var assert = database.CreateContext();
        var row = await assert.Set<MetadataRematerializationJobEntity>().SingleAsync();
        row.ExecutionFenceToken.ShouldBe(claimed.FenceToken);
        row.HangfireJobId.ShouldBe("competing-delivery");
        row.NextAttemptAtUtc.ShouldBeNull();
        row.CursorTitleId.ShouldBe(1);
        (await assert.JobDispatches.SingleAsync()).DeliveredAtUtc.ShouldNotBeNull();
        (await assert.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }
    private sealed class ClaimGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE romd.\"Jobs\"", StringComparison.Ordinal) && command.CommandText.Contains("ExecutionFenceToken", StringComparison.Ordinal) && !Reached.Task.IsCompleted)
            { Reached.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }

    private static ApplyStateContext FailedContext(Guid id, string delivery)
    {
        var invocation = Hangfire.Common.Job.FromExpression<Entry>(x => x.Run(id));
        return new(Substitute.For<JobStorage>(), Substitute.For<IStorageConnection>(), Substitute.For<IWriteOnlyTransaction>(),
            new BackgroundJob(delivery, invocation, DateTime.UtcNow), new FailedState(new IOException("exhausted")), ProcessingState.StateName);
    }
    public sealed class Entry { public void Run(Guid id) { } }
}
