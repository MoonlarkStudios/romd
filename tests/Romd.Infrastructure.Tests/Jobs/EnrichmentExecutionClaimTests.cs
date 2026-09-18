using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class EnrichmentExecutionClaimTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 26, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task BulkClaim_CrashAfterClaim_SameDeliveryCanReclaimButDistinctDeliveryCannot()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = Options(database);
        var timeProvider = new ManualTimeProvider(InitialTime);
        var claimOptions = new JobExecutionClaimOptions();

        try
        {
            BulkEnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                await SeedCatalogAsync(seedContext);
                job = BulkEnrichmentJob.Create(3, "snes");
                job.SetHangfireJobId("delivery-a");
                await new BulkEnrichmentJobRepository(seedContext, timeProvider, claimOptions).AddAsync(job);
            }

            await using (var claimContext = new RomdDbContext(options))
            {
                var repository = new BulkEnrichmentJobRepository(claimContext, timeProvider, claimOptions);
                (await repository.TryClaimExecutionAsync(job.Id, "delivery-a")).ShouldNotBeNull();
            }

            await using var redeliveryContext = new RomdDbContext(options);
            var redeliveryRepository = new BulkEnrichmentJobRepository(redeliveryContext, timeProvider, claimOptions);
            (await redeliveryRepository.TryClaimExecutionAsync(job.Id, "delivery-a")).ShouldBeNull();
            (await redeliveryRepository.TryClaimExecutionAsync(job.Id, "delivery-b")).ShouldBeNull();

            // The worker dies after the atomic transition and before its next checkpoint.
            timeProvider.Advance(claimOptions.LeaseDuration);
            (await redeliveryRepository.TryClaimExecutionAsync(job.Id, "delivery-b")).ShouldBeNull();
            var reclaimed = await redeliveryRepository.TryClaimExecutionAsync(job.Id, "delivery-a");
            reclaimed.ShouldNotBeNull();
            reclaimed.Job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Enriching);
            reclaimed.Job.HangfireJobId.ShouldBe("delivery-a");
            reclaimed.Job.StartedAt.ShouldBe(InitialTime);
            reclaimed.FenceToken.ShouldNotBe(Guid.Empty);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task SingleRunner_ConcurrentDistinctDeliveries_ExecutesJobOnlyOnce()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var barrier = new ReadBarrier(2);
        var services = new ServiceCollection()
            .AddDbContext<RomdDbContext>(options => options
                .UseNpgsql(database.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(barrier)
            .AddSingleton<ClaimUpdateProbe>()
            .AddScoped<IJobRepository<EnrichmentJob>>(sp =>
                new BarrierEnrichmentJobRepository(
                    new EnrichmentJobRepository(
                        sp.GetRequiredService<RomdDbContext>(),
                        sp.GetRequiredService<TimeProvider>()),
                    sp.GetRequiredService<ReadBarrier>(),
                    sp.GetRequiredService<ClaimUpdateProbe>()))
            .AddScoped<JobAuditContext>()
            .AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
        var provider = services.BuildServiceProvider();

        try
        {
            EnrichmentJob job;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var context = seedScope.ServiceProvider.GetRequiredService<RomdDbContext>();
                await SeedCatalogAsync(context);
                job = EnrichmentJob.Create("Chrono", 7, 3, TimeProvider.System);
                await new EnrichmentJobRepository(context, TimeProvider.System).AddAsync(job);
            }

            var claimUpdateProbe = provider.GetRequiredService<ClaimUpdateProbe>();
            var executor = new CountingEnrichmentExecutor(claimUpdateProbe);
            var notifier = Substitute.For<IJobNotifier>();
            var romdOptions = Substitute.For<IRomdOptions>();
            romdOptions.DataDirectory.Returns(Path.GetTempPath());
            var runner = new JobRunner<EnrichmentJob>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                executor,
                notifier,
                romdOptions,
                NullLogger<JobRunner<EnrichmentJob>>.Instance);

            await Task.WhenAll(
                runner.RunAsync(job.Id, "delivery-a", CancellationToken.None),
                runner.RunAsync(job.Id, "delivery-b", CancellationToken.None));

            executor.ExecutionCount.ShouldBe(1);
            executor.UpdateCountAtExecution.ShouldBe(0);
            claimUpdateProbe.UpdateCount.ShouldBe(1);
            await using var resultScope = provider.CreateAsyncScope();
            var persisted = await resultScope.ServiceProvider
                .GetRequiredService<RomdDbContext>()
                .Jobs
                .OfType<EnrichmentJobEntity>()
                .SingleAsync(entity => entity.Id == job.Id);
            persisted.Phase.ShouldBe(EnrichmentJobPhase.Completed.ToString());
            persisted.HangfireJobId.ShouldBeOneOf("delivery-a", "delivery-b");
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleClaim_ConcurrentDistinctDeliveries_ReturnsExactlyOneWinner()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = Options(database);
        var timeProvider = new ManualTimeProvider(InitialTime);

        try
        {
            EnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                await SeedCatalogAsync(seedContext);
                job = EnrichmentJob.Create("Chrono", 7, 3, timeProvider);
                await new EnrichmentJobRepository(seedContext, timeProvider).AddAsync(job);
            }

            await using var firstContext = new RomdDbContext(options);
            await using var secondContext = new RomdDbContext(options);
            IJobExecutionClaimRepository<EnrichmentJob> firstRepository =
                new EnrichmentJobRepository(firstContext, timeProvider);
            IJobExecutionClaimRepository<EnrichmentJob> secondRepository =
                new EnrichmentJobRepository(secondContext, timeProvider);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<JobExecutionClaim<EnrichmentJob>?> ClaimAsync(
                IJobExecutionClaimRepository<EnrichmentJob> repository,
                string deliveryId)
            {
                await start.Task;
                return await repository.TryClaimExecutionAsync(job.Id, deliveryId);
            }

            Task<JobExecutionClaim<EnrichmentJob>?> firstClaim = ClaimAsync(firstRepository, "delivery-a");
            Task<JobExecutionClaim<EnrichmentJob>?> secondClaim = ClaimAsync(secondRepository, "delivery-b");
            start.SetResult();
            var claims = await Task.WhenAll(firstClaim, secondClaim);

            var winner = claims.Where(claim => claim is not null).ShouldHaveSingleItem()!;
            winner.Job.PhaseEnum.ShouldBe(EnrichmentJobPhase.Enriching);
            winner.Job.HangfireJobId.ShouldBeOneOf("delivery-a", "delivery-b");
            claims.Count(claim => claim is null).ShouldBe(1);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task Heartbeat_LiveExecutionPastLease_SameDeliveryCannotReclaim()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var timeProvider = new ManualTimeProvider(InitialTime);
        var claimOptions = new JobExecutionClaimOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(10)
        };
        var executor = new BlockingEnrichmentExecutor();
        var heartbeatGate = new HeartbeatRenewalGate();
        var services = new ServiceCollection()
            .AddDbContext<RomdDbContext>(options => options
                .UseNpgsql(database.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking))
            .AddSingleton<TimeProvider>(timeProvider)
            .AddSingleton(claimOptions)
            .AddSingleton(heartbeatGate)
            .AddScoped<IJobRepository<EnrichmentJob>>(sp =>
                new HeartbeatGateEnrichmentJobRepository(
                    new EnrichmentJobRepository(
                        sp.GetRequiredService<RomdDbContext>(),
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetRequiredService<JobExecutionClaimOptions>()),
                    sp.GetRequiredService<HeartbeatRenewalGate>()))
            .AddScoped<JobAuditContext>()
            .AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
        await using var provider = services.BuildServiceProvider();

        try
        {
            EnrichmentJob job;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var context = seedScope.ServiceProvider.GetRequiredService<RomdDbContext>();
                await SeedCatalogAsync(context);
                job = EnrichmentJob.Create("Chrono", 7, 3, timeProvider);
                await new EnrichmentJobRepository(context, timeProvider, claimOptions).AddAsync(job);
            }

            var notifier = Substitute.For<IJobNotifier>();
            var romdOptions = Substitute.For<IRomdOptions>();
            romdOptions.DataDirectory.Returns(Path.GetTempPath());
            var runner = new JobRunner<EnrichmentJob>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                executor,
                notifier,
                romdOptions,
                timeProvider,
                claimOptions,
                NullLogger<JobRunner<EnrichmentJob>>.Instance);

            Task running = runner.RunAsync(job.Id, "delivery-a", CancellationToken.None);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await heartbeatGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Renew while the original claim is live, then move deterministically past its
            // original lease boundary. The completed-renewal gate proves the runner extended
            // the lease before either contender is allowed to inspect it.
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            heartbeatGate.Release.SetResult();
            (await heartbeatGate.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
            var originalLeaseBoundary = InitialTime + claimOptions.LeaseDuration;
            timeProvider.SetUtcNow(originalLeaseBoundary + TimeSpan.FromTicks(1));
            timeProvider.GetUtcNow().ShouldBeGreaterThan(originalLeaseBoundary);

            await using (var contenderScope = provider.CreateAsyncScope())
            {
                var contender = (IJobExecutionClaimRepository<EnrichmentJob>)contenderScope.ServiceProvider
                    .GetRequiredService<IJobRepository<EnrichmentJob>>();
                (await contender.TryClaimExecutionAsync(job.Id, "delivery-a")).ShouldBeNull();
                (await contender.TryClaimExecutionAsync(job.Id, "delivery-b")).ShouldBeNull();
            }

            executor.Release.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
        }
    }

    [Fact]
    public async Task HostShutdown_AfterClaim_PreservesActiveJobForSameDeliveryRedelivery()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var claimOptions = new JobExecutionClaimOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(240),
            HeartbeatInterval = TimeSpan.FromMilliseconds(40)
        };
        var executor = new BlockingEnrichmentExecutor();
        var services = new ServiceCollection()
            .AddDbContext<RomdDbContext>(options => options
                .UseNpgsql(database.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(claimOptions)
            .AddScoped<IJobRepository<EnrichmentJob>>(sp =>
                new EnrichmentJobRepository(
                    sp.GetRequiredService<RomdDbContext>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<JobExecutionClaimOptions>()))
            .AddScoped<JobAuditContext>()
            .AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
        await using var provider = services.BuildServiceProvider();

        try
        {
            EnrichmentJob job;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var context = seedScope.ServiceProvider.GetRequiredService<RomdDbContext>();
                await SeedCatalogAsync(context);
                job = EnrichmentJob.Create("Chrono", 7, 3, TimeProvider.System);
                await new EnrichmentJobRepository(context, TimeProvider.System, claimOptions).AddAsync(job);
            }

            var romdOptions = Substitute.For<IRomdOptions>();
            romdOptions.DataDirectory.Returns(Path.GetTempPath());
            var runner = new JobRunner<EnrichmentJob>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                executor,
                Substitute.For<IJobNotifier>(),
                romdOptions,
                TimeProvider.System,
                claimOptions,
                NullLogger<JobRunner<EnrichmentJob>>.Instance);
            using var shutdown = new CancellationTokenSource();

            Task running = runner.RunAsync(job.Id, "delivery-a", shutdown.Token);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(5)));

            await using (var inspectScope = provider.CreateAsyncScope())
            {
                var entity = await inspectScope.ServiceProvider.GetRequiredService<RomdDbContext>()
                    .Jobs.OfType<EnrichmentJobEntity>().SingleAsync(candidate => candidate.Id == job.Id);
                entity.Phase.ShouldBe(EnrichmentJobPhase.Enriching.ToString());
                entity.ExecutionFenceToken.ShouldNotBeNull();
            }

            await Task.Delay(claimOptions.LeaseDuration + claimOptions.HeartbeatInterval * 2);
            await using var reclaimScope = provider.CreateAsyncScope();
            var reclaimRepository = (IJobExecutionClaimRepository<EnrichmentJob>)reclaimScope.ServiceProvider
                .GetRequiredService<IJobRepository<EnrichmentJob>>();
            (await reclaimRepository.TryClaimExecutionAsync(job.Id, "delivery-b")).ShouldBeNull();
            (await reclaimRepository.TryClaimExecutionAsync(job.Id, "delivery-a")).ShouldNotBeNull();
        }
        finally
        {
        }
    }

    [Fact]
    public async Task ClaimedCheckpoint_AfterLeaseHandoff_CannotOverwriteNewOwner()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = Options(database);
        var timeProvider = new ManualTimeProvider(InitialTime);
        var claimOptions = new JobExecutionClaimOptions();

        try
        {
            EnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                await SeedCatalogAsync(seedContext);
                job = EnrichmentJob.Create("Chrono", 7, 3, timeProvider);
                await new EnrichmentJobRepository(seedContext, timeProvider, claimOptions).AddAsync(job);
            }

            JobExecutionClaim<EnrichmentJob> first;
            await using (var firstContext = new RomdDbContext(options))
            {
                var firstRepository = new EnrichmentJobRepository(firstContext, timeProvider, claimOptions);
                first = (await firstRepository.TryClaimExecutionAsync(job.Id, "delivery-a"))!;
            }

            timeProvider.Advance(claimOptions.LeaseDuration);
            await using var secondContext = new RomdDbContext(options);
            var secondRepository = new EnrichmentJobRepository(secondContext, timeProvider, claimOptions);
            var second = (await secondRepository.TryClaimExecutionAsync(job.Id, "delivery-a"))!;
            second.FenceToken.ShouldNotBe(first.FenceToken);

            first.Job.Complete();
            await using (var staleContext = new RomdDbContext(options))
            {
                var staleRepository = new EnrichmentJobRepository(staleContext, timeProvider, claimOptions);
                (await staleRepository.TryUpdateClaimedAsync(first.Job, first.FenceToken)).ShouldBeFalse();
            }

            second.Job.Complete();
            (await secondRepository.TryUpdateClaimedAsync(second.Job, second.FenceToken)).ShouldBeTrue();
            var persisted = await secondRepository.GetByIdAsync(job.Id);
            persisted!.PhaseEnum.ShouldBe(EnrichmentJobPhase.Completed);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task ExplicitCancellation_ClearsFenceAndRejectsClaimedRunnerCheckpoint()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = Options(database);
        var timeProvider = new ManualTimeProvider(InitialTime);
        var claimOptions = new JobExecutionClaimOptions();

        try
        {
            EnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                await SeedCatalogAsync(seedContext);
                job = EnrichmentJob.Create("Chrono", 7, 3, timeProvider);
                await new EnrichmentJobRepository(seedContext, timeProvider, claimOptions).AddAsync(job);
            }

            JobExecutionClaim<EnrichmentJob> claim;
            await using (var claimContext = new RomdDbContext(options))
            {
                claim = (await new EnrichmentJobRepository(claimContext, timeProvider, claimOptions)
                    .TryClaimExecutionAsync(job.Id, "delivery-a"))!;
            }

            await using (var cancelContext = new RomdDbContext(options))
            {
                var endpointRepository = new JobRepository(cancelContext, timeProvider);
                var cancelled = (await endpointRepository.GetByIdAsync(job.Id))!;
                cancelled.Cancel();
                await endpointRepository.UpdateAsync(cancelled);
            }

            claim.Job.SetCurrentItem("stale checkpoint");
            await using (var staleContext = new RomdDbContext(options))
            {
                var staleRepository = new EnrichmentJobRepository(staleContext, timeProvider, claimOptions);
                (await staleRepository.TryUpdateClaimedAsync(claim.Job, claim.FenceToken)).ShouldBeFalse();
                var persisted = await staleRepository.GetByIdAsync(job.Id);
                persisted!.PhaseEnum.ShouldBe(EnrichmentJobPhase.Cancelled);
                persisted.CurrentItem.ShouldBeNull();
            }
        }
        finally
        {
        }
    }

    [Fact]
    public async Task OwnedMutation_EndpointCancellationWinsRace_MutationNeverRuns()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = Options(database);
        var timeProvider = new ManualTimeProvider(InitialTime);
        var claimOptions = new JobExecutionClaimOptions();

        try
        {
            EnrichmentJob job;
            await using (var seedContext = new RomdDbContext(options))
            {
                await SeedCatalogAsync(seedContext);
                job = EnrichmentJob.Create("Chrono", 7, 3, timeProvider);
                await new EnrichmentJobRepository(seedContext, timeProvider, claimOptions).AddAsync(job);
            }

            JobExecutionClaim<EnrichmentJob> claim;
            await using (var claimContext = new RomdDbContext(options))
            {
                claim = (await new EnrichmentJobRepository(claimContext, timeProvider, claimOptions)
                    .TryClaimExecutionAsync(job.Id, "delivery-a"))!;
            }

            await using var cancelContext = new RomdDbContext(options);
            await using var cancelTransaction = await cancelContext.Database.BeginTransactionAsync();
            var endpointRepository = new JobRepository(cancelContext, timeProvider);
            var cancelled = (await endpointRepository.GetByIdAsync(job.Id))!;
            cancelled.Cancel();
            await endpointRepository.UpdateAsync(cancelled);

            bool mutationRan = false;
            await using var mutationContext = new RomdDbContext(options);
            var guard = new JobExecutionMutationGuard(
                mutationContext,
                new EfUnitOfWork(mutationContext),
                timeProvider,
                claimOptions);
            var guardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task guardedMutation = Task.Run(async () =>
            {
                guardStarted.SetResult();
                await guard.ExecuteAsync(
                    job.Id,
                    claim.FenceToken,
                    _ =>
                    {
                        mutationRan = true;
                        return Task.CompletedTask;
                    });
            });

            await guardStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            guardedMutation.IsCompleted.ShouldBeFalse();
            await cancelTransaction.CommitAsync();

            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => guardedMutation);
            mutationRan.ShouldBeFalse();
        }
        finally
        {
        }
    }

    private static DbContextOptions<RomdDbContext> Options(PostgreSqlTestDatabase database) =>
        new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

    private static async Task SeedCatalogAsync(RomdDbContext context)
    {
        context.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = InitialTime
        });
        context.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 3,
            Name = "Chrono",
            NormalizedName = "chrono",
            EnrichmentStatus = "None",
            CreatedAt = InitialTime,
            CreatedByUserId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();
    }


    private sealed class BarrierEnrichmentJobRepository(
        EnrichmentJobRepository inner,
        ReadBarrier barrier,
        ClaimUpdateProbe updateProbe) : IJobExecutionClaimRepository<EnrichmentJob>
    {
        public async Task<EnrichmentJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            var job = await inner.GetByIdAsync(id, ct);
            await barrier.SignalAndWaitAsync(ct);
            return job;
        }

        public Task<IReadOnlyList<EnrichmentJob>> GetActiveAsync(CancellationToken ct = default) =>
            inner.GetActiveAsync(ct);

        public Task AddAsync(EnrichmentJob job, CancellationToken ct = default) =>
            inner.AddAsync(job, ct);

        public Task UpdateAsync(EnrichmentJob job, CancellationToken ct = default)
        {
            updateProbe.RecordUpdate();
            return inner.UpdateAsync(job, ct);
        }

        public Task<JobExecutionClaim<EnrichmentJob>?> TryClaimExecutionAsync(
            Guid jobId,
            string deliveryId,
            CancellationToken ct = default) =>
            inner.TryClaimExecutionAsync(jobId, deliveryId, ct);

        public Task<bool> RenewExecutionLeaseAsync(
            Guid jobId,
            Guid fenceToken,
            CancellationToken ct = default) =>
            inner.RenewExecutionLeaseAsync(jobId, fenceToken, ct);

        public Task<bool> HasExecutionOwnershipAsync(
            Guid jobId,
            Guid fenceToken,
            CancellationToken ct = default) =>
            inner.HasExecutionOwnershipAsync(jobId, fenceToken, ct);

        public Task<bool> TryUpdateClaimedAsync(
            EnrichmentJob job,
            Guid fenceToken,
            CancellationToken ct = default)
        {
            updateProbe.RecordUpdate();
            return inner.TryUpdateClaimedAsync(job, fenceToken, ct);
        }
    }

    private sealed class HeartbeatGateEnrichmentJobRepository(
        EnrichmentJobRepository inner,
        HeartbeatRenewalGate heartbeatGate) : IJobExecutionClaimRepository<EnrichmentJob>
    {
        public Task<EnrichmentJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<EnrichmentJob>> GetActiveAsync(CancellationToken ct = default) =>
            inner.GetActiveAsync(ct);

        public Task AddAsync(EnrichmentJob job, CancellationToken ct = default) =>
            inner.AddAsync(job, ct);

        public Task UpdateAsync(EnrichmentJob job, CancellationToken ct = default) =>
            inner.UpdateAsync(job, ct);

        public Task<JobExecutionClaim<EnrichmentJob>?> TryClaimExecutionAsync(
            Guid jobId,
            string deliveryId,
            CancellationToken ct = default) =>
            inner.TryClaimExecutionAsync(jobId, deliveryId, ct);

        public async Task<bool> RenewExecutionLeaseAsync(
            Guid jobId,
            Guid fenceToken,
            CancellationToken ct = default)
        {
            bool isGatedRenewal = await heartbeatGate.WaitForReleaseAsync(ct);
            bool renewed = await inner.RenewExecutionLeaseAsync(jobId, fenceToken, ct);
            if (isGatedRenewal)
            {
                heartbeatGate.Completed.TrySetResult(renewed);
            }

            return renewed;
        }

        public Task<bool> HasExecutionOwnershipAsync(
            Guid jobId,
            Guid fenceToken,
            CancellationToken ct = default) =>
            inner.HasExecutionOwnershipAsync(jobId, fenceToken, ct);

        public Task<bool> TryUpdateClaimedAsync(
            EnrichmentJob job,
            Guid fenceToken,
            CancellationToken ct = default) =>
            inner.TryUpdateClaimedAsync(job, fenceToken, ct);
    }

    private sealed class HeartbeatRenewalGate
    {
        private int _renewalCount;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> WaitForReleaseAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _renewalCount) != 1)
            {
                return false;
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return true;
        }
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

    private sealed class ClaimUpdateProbe
    {
        private int _updateCount;

        public int UpdateCount => Volatile.Read(ref _updateCount);

        public void RecordUpdate() => Interlocked.Increment(ref _updateCount);
    }

    private sealed class CountingEnrichmentExecutor(ClaimUpdateProbe updateProbe)
        : IJobExecutor<EnrichmentJob>
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public int UpdateCountAtExecution { get; private set; }

        public Task ExecuteAsync(EnrichmentJob job, JobContext context)
        {
            UpdateCountAtExecution = updateProbe.UpdateCount;
            Interlocked.Increment(ref _executionCount);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingEnrichmentExecutor : IJobExecutor<EnrichmentJob>
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(EnrichmentJob job, JobContext context)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(context.CancellationToken);
        }
    }
}
