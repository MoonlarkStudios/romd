using System.Reflection;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class EnrichmentSchedulingRecoveryTests
{
    [Fact]
    public async Task JobRunner_ConcurrentBulkDeliveries_ExecutesJobOnlyOnce()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var barrier = new ReadBarrier(participantCount: 2);
        var services = new ServiceCollection()
            .AddDbContext<RomdDbContext>(options => options
                .UseNpgsql(database.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(barrier)
            .AddScoped<IJobRepository<BulkEnrichmentJob>>(sp =>
                new BarrierBulkJobRepository(
                    new BulkEnrichmentJobRepository(
                        sp.GetRequiredService<RomdDbContext>(),
                        sp.GetRequiredService<TimeProvider>()),
                    sp.GetRequiredService<ReadBarrier>()))
            .AddScoped<JobAuditContext>()
            .AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());

        var provider = services.BuildServiceProvider();
        BulkEnrichmentJob job;
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<RomdDbContext>();
            context.Platforms.Add(NewPlatformEntity(3, "Super Nintendo", "snes"));
            await context.SaveChangesAsync();
            job = BulkEnrichmentJob.Create(3, "snes");
            await new BulkEnrichmentJobRepository(context, TimeProvider.System).AddAsync(job);
        }

        var executor = new CountingBulkExecutor();
        var notifier = Substitute.For<IJobNotifier>();
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetTempPath());
        var runner = new JobRunner<BulkEnrichmentJob>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            executor,
            notifier,
            options,
            NullLogger<JobRunner<BulkEnrichmentJob>>.Instance);

        try
        {
            await Task.WhenAll(
                runner.RunAsync(job.Id, "hangfire-a", CancellationToken.None),
                runner.RunAsync(job.Id, "hangfire-b", CancellationToken.None));

            executor.ExecutionCount.ShouldBe(1);
            await using var resultScope = provider.CreateAsyncScope();
            var persisted = await resultScope.ServiceProvider
                .GetRequiredService<RomdDbContext>()
                .Jobs
                .OfType<BulkEnrichmentJobEntity>()
                .SingleAsync(entity => entity.Id == job.Id);
            persisted.Phase.ShouldBe(BulkEnrichmentPhase.Completed.ToString());
            persisted.HangfireJobId.ShouldBeOneOf("hangfire-a", "hangfire-b");
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryClaimExecutionAsync_ConcurrentClaims_ReturnsExactlyOneWinner()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        try
        {
            BulkEnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                seedContext.Platforms.Add(NewPlatformEntity(3, "Super Nintendo", "snes"));
                await seedContext.SaveChangesAsync();
                job = BulkEnrichmentJob.Create(3, "snes");
                job.SetTotalTitles(25);
                await new BulkEnrichmentJobRepository(seedContext, TimeProvider.System).AddAsync(job);
            }

            await using var firstContext = new RomdDbContext(options);
            await using var secondContext = new RomdDbContext(options);
            var firstRepository = new BulkEnrichmentJobRepository(firstContext, TimeProvider.System);
            var secondRepository = new BulkEnrichmentJobRepository(secondContext, TimeProvider.System);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<JobExecutionClaim<BulkEnrichmentJob>?> ClaimAsync(
                BulkEnrichmentJobRepository repository,
                string hangfireJobId)
            {
                await start.Task;
                return await repository.TryClaimExecutionAsync(job.Id, hangfireJobId);
            }

            Task<JobExecutionClaim<BulkEnrichmentJob>?> firstClaim = ClaimAsync(firstRepository, "hangfire-a");
            Task<JobExecutionClaim<BulkEnrichmentJob>?> secondClaim = ClaimAsync(secondRepository, "hangfire-b");
            start.SetResult();
            var claims = await Task.WhenAll(firstClaim, secondClaim);

            var winner = claims.Where(claim => claim is not null).ShouldHaveSingleItem()!;
            winner.Job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Enriching);
            winner.Job.StartedAt.ShouldNotBeNull();
            winner.Job.HangfireJobId.ShouldBeOneOf("hangfire-a", "hangfire-b");
            winner.Job.TotalTitles.ShouldBe(25);
            winner.Job.ProcessedCount.ShouldBe(0);
            claims.Count(claim => claim is null).ShouldBe(1);
        }
        finally
        {
        }
    }

    private sealed class BarrierBulkJobRepository(
        BulkEnrichmentJobRepository inner,
        ReadBarrier barrier) : IJobExecutionClaimRepository<BulkEnrichmentJob>
    {
        // Ordinary reads synchronize both deliveries so removing/bypassing the atomic-claim
        // capability deterministically reproduces the former double execution. The claim path
        // deliberately delegates straight to the real SQLite conditional update.
        public async Task<BulkEnrichmentJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            var job = await inner.GetByIdAsync(id, ct);
            await barrier.SignalAndWaitAsync(ct);
            return job;
        }

        public Task<IReadOnlyList<BulkEnrichmentJob>> GetActiveAsync(CancellationToken ct = default) =>
            inner.GetActiveAsync(ct);

        public Task AddAsync(BulkEnrichmentJob job, CancellationToken ct = default) =>
            inner.AddAsync(job, ct);

        public Task UpdateAsync(BulkEnrichmentJob job, CancellationToken ct = default) =>
            inner.UpdateAsync(job, ct);

        public Task<JobExecutionClaim<BulkEnrichmentJob>?> TryClaimExecutionAsync(
            Guid jobId,
            string deliveryId,
            CancellationToken ct = default) =>
            inner.TryClaimExecutionAsync(jobId, deliveryId, ct);

        public Task<bool> RenewExecutionLeaseAsync(Guid jobId, Guid fenceToken, CancellationToken ct = default) =>
            inner.RenewExecutionLeaseAsync(jobId, fenceToken, ct);

        public Task<bool> HasExecutionOwnershipAsync(Guid jobId, Guid fenceToken, CancellationToken ct = default) =>
            inner.HasExecutionOwnershipAsync(jobId, fenceToken, ct);

        public Task<bool> TryUpdateClaimedAsync(BulkEnrichmentJob job, Guid fenceToken, CancellationToken ct = default) =>
            inner.TryUpdateClaimedAsync(job, fenceToken, ct);
    }

    private sealed class ReadBarrier(int participantCount)
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == participantCount)
            {
                _reached.TrySetResult();
            }

            return _reached.Task.WaitAsync(ct);
        }
    }

    private sealed class CountingBulkExecutor : IJobExecutor<BulkEnrichmentJob>
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task ExecuteAsync(BulkEnrichmentJob job, JobContext context)
        {
            Interlocked.Increment(ref _executionCount);
            return Task.CompletedTask;
        }
    }

    private static PlatformEntity NewPlatformEntity(int id, string name, string shortName) => new()
    {
        Id = id,
        Name = name,
        Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = name, BaseCompactLabel = name, CanonicalKey = shortName, ShortName = shortName,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
