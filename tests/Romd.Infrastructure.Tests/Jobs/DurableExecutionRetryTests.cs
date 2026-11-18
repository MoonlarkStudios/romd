using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class DurableExecutionRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_FailedReplaceDatExecution_RearmsUntilSuccessOrRetryBudgetExhaustion(bool succeedsOnThirdAttempt)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var job = ReplaceDatJob.Create(7, "replacement.dat", clock.GetUtcNow());
        await using (var seed = database.CreateContext())
        {
            seed.Add(ReplaceDatJobEntity.FromDomain(job));
            await seed.SaveChangesAsync();
        }

        var options = new JobExecutionClaimOptions();
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(options);
        services.AddScoped<ReplaceDatJobRepository>();
        services.AddScoped<IJobRepository<ReplaceDatJob>, ClaimedJobRepository<ReplaceDatJob, ReplaceDatJobRepository>>();
        services.AddScoped<IAuditContext, JobAuditContext>();
        services.AddRomdJobLifecyclePolicies();
        await using var provider = services.BuildServiceProvider();

        var romd = Substitute.For<IRomdOptions>();
        romd.DataDirectory.Returns(Path.GetTempPath());
        var executor = Substitute.For<IResumableJobExecutor<ReplaceDatJob>>();
        int executions = 0;
        executor.ExecuteAsync(Arg.Any<ReplaceDatJob>(), Arg.Any<JobContext>())
            .Returns(call => ExecuteAsync(call.Arg<ReplaceDatJob>(), call.Arg<JobContext>()));
        var runner = new JobRunner<ReplaceDatJob>(provider.GetRequiredService<IServiceScopeFactory>(),
            executor, new NoOpJobNotifier(), romd, clock, options, NullLogger<JobRunner<ReplaceDatJob>>.Instance);
        await using var observer = database.CreateContext();
        var dispatches = new JobDispatchRepository(observer, clock);
        var transport = Substitute.For<IBackgroundJobClient>();
        transport.Create(Arg.Any<Hangfire.Common.Job>(), Arg.Any<IState>())
            .Returns("delivery-1", "delivery-2", "delivery-3");
        var dispatcher = new JobDispatchService(dispatches, transport, NullLogger<JobDispatchService>.Instance);
        DateTimeOffset startedAt = clock.GetUtcNow();

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            string delivery = $"delivery-{attempt}";
            await dispatcher.DispatchAsync(job.Id);
            (await observer.JobDispatches.SingleAsync()).HangfireJobId.ShouldBe(delivery);
            await runner.RunAsync(job.Id, delivery, CancellationToken.None);
            executions.ShouldBe(attempt);
            var row = await observer.Set<ReplaceDatJobEntity>().SingleAsync();
            row.StartedAt.ShouldBe(startedAt);
            row.NewDatId.ShouldBe(42);
            row.ExecutionFenceToken.ShouldBeNull();
            row.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
            int expectedErrors = succeedsOnThirdAttempt && attempt == 3 ? 2 : attempt;
            row.GetErrors().Count(error => error.Item == "attempt").ShouldBe(expectedErrors);

            if (attempt == 3)
            {
                row.Phase.ShouldBe(succeedsOnThirdAttempt ? "CompletedWithErrors" : "Failed");
                row.NextAttemptAtUtc.ShouldBeNull();
                row.CompletedAt.ShouldNotBeNull();
                clock.Advance(options.LeaseDuration);
                (await dispatches.GetAvailableAsync(25, default)).ShouldBeEmpty();
                continue;
            }

            var retryAt = clock.GetUtcNow().AddSeconds(attempt * 30);
            row.Phase.ShouldBe("Replacing");
            row.HangfireJobId.ShouldBeNull();
            row.NextAttemptAtUtc.ShouldBe(retryAt);
            row.DeferralCount.ShouldBe(attempt);
            row.FirstDeferralAt.ShouldBe(startedAt);
            var pending = await observer.JobDispatches.SingleAsync();
            pending.DeliveredAtUtc.ShouldBeNull();
            pending.AvailableAtUtc.ShouldBe(retryAt);
            pending.LastError.ShouldBe($"Transient failure {attempt}");
            clock.SetUtcNow(retryAt.AddSeconds(-1));
            (await dispatches.GetAvailableAsync(25, default)).ShouldBeEmpty();
            await runner.RunAsync(job.Id, delivery, CancellationToken.None);
            executions.ShouldBe(attempt);

            clock.SetUtcNow(retryAt);
            if (succeedsOnThirdAttempt && attempt == 2)
            {
                clock.Advance(TimeSpan.FromDays(2));
                var repository = new ReplaceDatJobRepository(observer, clock);
                var cutoff = clock.GetUtcNow().AddMinutes(-30);
                (await repository.GetStaleStartedIdsAsync(cutoff)).ShouldBeEmpty();
                await using var transaction = await new EfUnitOfWork(observer).BeginTransactionAsync();
                (await repository.TryFailStaleStartedStagedAsync(job.Id, cutoff, "stale")).ShouldBeFalse();
                await transaction.CommitAsync();
            }
            (await dispatches.GetAvailableAsync(25, default)).ShouldBe([job.Id]);
        }

        async Task ExecuteAsync(ReplaceDatJob current, JobContext context)
        {
            executions++;
            if (executions == 1)
            {
                current.SetNewDatId(42);
                current.BeginReplacing();
                await context.CheckpointAsync();
            }
            else
            {
                current.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
                current.NewDatId.ShouldBe(42);
            }
            if (!succeedsOnThirdAttempt || executions < 3)
                throw new IOException($"Transient failure {executions}");
        }
    }
}
