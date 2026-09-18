using System.Diagnostics;
using System.Net;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Search;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;
using static Romd.Hosting.IntegrationTests.Hosting.RomdHostProcesses;

namespace Romd.Hosting.IntegrationTests.Hosting;

[Collection(nameof(HangfirePostgreSqlColdStartCollection))]
public sealed class HangfirePostgreSqlColdStartTests
{
    [Fact]
    public async Task ApiProcesses_FailClosedUntilWorkerProcessPublishesSchema_ThenConverge()
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();

        string dataDirectory = CreateDataDirectory("romd-pg-cold-start");

        int adminPort = GetFreePort();
        int consumerPort = GetFreePort();
        using Process admin = StartAdmin(adminPort, dataDirectory, postgres.AdminConnectionString);
        using Process consumer = StartConsumer(consumerPort, dataDirectory, postgres.ConsumerConnectionString);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await WaitForStatusAsync(client, adminPort, "/health", HttpStatusCode.OK);
            await WaitForStatusAsync(client, consumerPort, "/health", HttpStatusCode.OK);
            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.ServiceUnavailable);
            await WaitForStatusAsync(client, consumerPort, "/health/ready", HttpStatusCode.ServiceUnavailable);

            using Process provisioner = StartProvisioner(dataDirectory, postgres.ProvisionerConnectionString);
            await provisioner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            provisioner.ExitCode.ShouldBe(0, RecentOutput(provisioner));

            // Hangfire alone does not open the API hosts: the application schema has its own gate,
            // and only the worker entrypoint may migrate it.
            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.ServiceUnavailable);
            using Process databaseProvisioner = StartDatabaseProvisioner(
                dataDirectory,
                postgres.ProvisionerConnectionString);
            await databaseProvisioner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            databaseProvisioner.ExitCode.ShouldBe(0, RecentOutput(databaseProvisioner));

            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.OK, TimeSpan.FromSeconds(15));
            await WaitForStatusAsync(client, consumerPort, "/health/ready", HttpStatusCode.OK, TimeSpan.FromSeconds(15));

            await using var connection = new Npgsql.NpgsqlConnection(postgres.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM hangfire.romd_schema WHERE singleton=TRUE AND version=1";
            Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(1);
            command.CommandText = "SELECT version FROM romd.romd_schema WHERE singleton=TRUE";
            Convert.ToInt32(await command.ExecuteScalarAsync())
                .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);

            await AssertExecutionRetryAndFailureSyncAsync(postgres.WorkerConnectionString);

            // A normal PostgreSQL worker must start and execute freshly accepted work.
            // Obsolete files are inert: startup neither reads them nor requires a cutover marker.
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hangfire.db"), "obsolete");
            var upload = UploadJob.Create("first-boot/");
            Directory.CreateDirectory(Path.Combine(dataDirectory, "temp", "jobs", upload.Id.ToString("N")));
            await using (var db = new RomdDbContext(CreateApplicationOptions(postgres.WorkerConnectionString)))
            {
                db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
                await db.SaveChangesAsync();
            }
            new BackgroundJobClient(CreateRuntimeStorage(postgres.WorkerConnectionString)).Create(
                Hangfire.Common.Job.FromExpression<Romd.Infrastructure.Jobs.Handlers.UploadJobHangfireHandler>(
                    handler => handler.ExecuteAsync(upload.Id, null!)),
                new Hangfire.States.EnqueuedState("upload"));
            await AssertNormalWorkerExecutesJobAsync(dataDirectory, postgres, upload.Id);
        }
        finally
        {
            Stop(admin);
            Stop(consumer);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static DbContextOptions<RomdDbContext> CreateApplicationOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>()
            .AddInterceptors(new SearchDocumentInterceptor());
        PostgreSqlConfiguration.Configure(builder, connectionString);
        return builder.Options;
    }

    private static async Task AssertNormalWorkerExecutesJobAsync(
        string dataDirectory,
        RomdPostgreSqlContainer postgres,
        Guid jobId)
    {
        using Process worker = StartWorker(
            dataDirectory,
            postgres.WorkerConnectionString,
            postgres.ProvisionerConnectionString);
        try
        {
            DbContextOptions<RomdDbContext> appOptions = CreateApplicationOptions(postgres.WorkerConnectionString);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                worker.HasExited.ShouldBeFalse(
                    $"the worker exited before executing the fresh job:{Environment.NewLine}{RecentOutput(worker)}");
                await using var db = new RomdDbContext(appOptions);
                UploadJobEntity job = await db.Jobs.OfType<UploadJobEntity>()
                    .SingleAsync(candidate => candidate.Id == jobId, budget.Token);
                if (job.Phase == UploadPhase.Completed.ToString())
                {
                    // The job completes its ROMD phase inside the Hangfire invocation; Hangfire records
                    // Succeeded only after the invocation returns.
                    await WaitForLatestStateAsync(
                        CreateRuntimeStorage(postgres.WorkerConnectionString),
                        job.HangfireJobId!,
                        "Succeeded");
                    return;
                }

                try
                {
                    await Task.Delay(250, budget.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"Fresh upload job stayed in phase {job.Phase}; last error: {job.LastAttemptError}");
                }
            }
        }
        finally
        {
            Stop(worker);
        }
    }

    private static async Task AssertExecutionRetryAndFailureSyncAsync(string connectionString)
    {
        PostgreSqlStorage storage = CreateRuntimeStorage(connectionString);
        JobActivator previousActivator = JobActivator.Current;
        JobActivator.Current = new JobActivator();
        var server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            WorkerCount = 1,
            Queues = ["default"],
            ServerName = "postgres-parity-test"
        }, storage);
        try
        {
            var client = new BackgroundJobClient(storage);

        UploadJob upload = UploadJob.Create("cutover.zip");
        Guid romdJobId = upload.Id;
        const int platformId = 7301;
        const int titleId = 7302;
        EnrichmentJob fencedJob = EnrichmentJob.Create(
            "PostgreSQL fence parity",
            titleId,
            platformId,
            TimeProvider.System);
        DbContextOptions<RomdDbContext> appOptions = CreateApplicationOptions(connectionString);
        await using (var db = new RomdDbContext(appOptions))
        {
            db.Platforms.Add(new PlatformEntity
            {
                Id = platformId,
                Name = "PostgreSQL parity platform",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation, BaseName = "PostgreSQL parity platform", BaseCompactLabel = "PostgreSQL parity platform", CanonicalKey = "local-pg-parity", ShortName = "pg-parity",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.NewGuid()
            });
            db.Titles.Add(new TitleEntity
            {
                Id = titleId,
                PlatformId = platformId,
                Name = "PostgreSQL fence parity",
                NormalizedName = "postgresql fence parity",
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.NewGuid()
            });
            db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
            db.Set<EnrichmentJobEntity>().Add(EnrichmentJobEntity.FromDomain(fencedJob));
            await db.SaveChangesAsync();
        }

            var services = new ServiceCollection();
            services.AddDbContext<RomdDbContext>(options =>
                PostgreSqlConfiguration.Configure(options, connectionString));
            using ServiceProvider provider = services.BuildServiceProvider();
            var filter = new HangfireJobStateSyncFilter(
                provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                NullLogger<HangfireJobStateSyncFilter>.Instance);
            GlobalJobFilters.Filters.Add(filter);
            try
            {
            string failureId = client.Schedule(() => PostgreSqlParityJobs.AlwaysFailWithRetry(romdJobId), TimeSpan.FromHours(1));
            await using (var deliveryDb = new RomdDbContext(appOptions))
            {
                await deliveryDb.Jobs.Where(job => job.Id == romdJobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.HangfireJobId, failureId));
            }
            client.ChangeState(failureId, new Hangfire.States.EnqueuedState());
            await WaitForRetryTerminalFailureAsync(storage, failureId);
            string[] history = storage.GetMonitoringApi().JobDetails(failureId).History
                .Select(entry => entry.StateName)
                .ToArray();
            history.Count(state => state == "Processing").ShouldBe(2);
            history.Count(state => state == "Failed").ShouldBe(2);
            await using var assertDb = new RomdDbContext(appOptions);
            UploadJobEntity persisted = await assertDb.Jobs.OfType<UploadJobEntity>()
                .SingleAsync(job => job.Id == romdJobId);
            persisted.Phase.ShouldBe("Failed");
            persisted.GetErrors().ShouldContain(error =>
                error.Item == "job" && !string.IsNullOrWhiteSpace(error.Message));

            string fenceId = client.Enqueue(() =>
                PostgreSqlParityJobs.AssertAbandonedLeaseHandoffAndStaleFenceAsync(
                    connectionString,
                    fencedJob.Id,
                    CancellationToken.None));
            await WaitForLatestStateAsync(storage, fenceId, "Succeeded");
            await using var fenceAssertDb = new RomdDbContext(appOptions);
            EnrichmentJobEntity fencedPersisted = await fenceAssertDb.Jobs
                .OfType<EnrichmentJobEntity>()
                .SingleAsync(job => job.Id == fencedJob.Id);
            fencedPersisted.Phase.ShouldBe(EnrichmentJobPhase.Completed.ToString());
            fencedPersisted.ExecutionFenceToken.ShouldBeNull();
            fencedPersisted.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
            }
            finally
            {
                GlobalJobFilters.Filters.Remove(filter);
            }
        }
        finally
        {
            server.Dispose();
            JobActivator.Current = previousActivator;
        }
    }

    private static async Task WaitForRetryTerminalFailureAsync(JobStorage storage, string jobId)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                string[] history = storage.GetMonitoringApi().JobDetails(jobId)?.History
                    .Select(entry => entry.StateName)
                    .ToArray() ?? [];
                if (history.FirstOrDefault() == "Failed"
                    && history.Count(state => state == "Processing") == 2)
                    return;
                await Task.Delay(100, budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            string observed = string.Join(",", storage.GetMonitoringApi().JobDetails(jobId)?.History
                .Select(entry => entry.StateName) ?? []);
            throw new TimeoutException(
                $"Hangfire job {jobId} did not complete two failed attempts; observed history: {observed}");
        }
    }

}

public static class PostgreSqlParityJobs
{
    [AutomaticRetry(
        Attempts = 1,
        DelaysInSeconds = [1],
        OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void AlwaysFailWithRetry(Guid jobId) =>
        throw new InvalidOperationException("postgres parity failure");

    [AutomaticRetry(Attempts = 0)]
    public static async Task AssertAbandonedLeaseHandoffAndStaleFenceAsync(
        string connectionString,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        PostgreSqlConfiguration.Configure(builder, connectionString);
        DbContextOptions<RomdDbContext> options = builder.Options;
        var claimOptions = new JobExecutionClaimOptions();

        JobExecutionClaim<EnrichmentJob> first;
        await using (var firstContext = new RomdDbContext(options))
        {
            var firstRepository = new EnrichmentJobRepository(
                firstContext,
                TimeProvider.System,
                claimOptions);
            first = await firstRepository.TryClaimExecutionAsync(
                jobId,
                "postgres-delivery",
                cancellationToken)
                ?? throw new InvalidOperationException("Initial enrichment claim was rejected.");
        }

        await using (var expireContext = new RomdDbContext(options))
        {
            await expireContext.Jobs
                .OfType<EnrichmentJobEntity>()
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        job => job.ExecutionLeaseExpiresAtUtc,
                        DateTimeOffset.UtcNow.AddMinutes(-1)),
                    cancellationToken);
        }

        JobExecutionClaim<EnrichmentJob> handoff;
        await using (var handoffContext = new RomdDbContext(options))
        {
            var handoffRepository = new EnrichmentJobRepository(
                handoffContext,
                TimeProvider.System,
                claimOptions);
            handoff = await handoffRepository.TryClaimExecutionAsync(
                jobId,
                "postgres-delivery",
                cancellationToken)
                ?? throw new InvalidOperationException("Expired enrichment lease was not handed off.");
        }

        if (handoff.FenceToken == first.FenceToken)
            throw new InvalidOperationException("Lease handoff reused the stale fence token.");

        first.Job.Complete();
        await using (var staleContext = new RomdDbContext(options))
        {
            var staleRepository = new EnrichmentJobRepository(
                staleContext,
                TimeProvider.System,
                claimOptions);
            if (await staleRepository.TryUpdateClaimedAsync(
                    first.Job,
                    first.FenceToken,
                    cancellationToken))
            {
                throw new InvalidOperationException("Stale fence overwrote the handoff owner.");
            }
        }

        handoff.Job.Complete();
        await using var ownerContext = new RomdDbContext(options);
        var ownerRepository = new EnrichmentJobRepository(
            ownerContext,
            TimeProvider.System,
            claimOptions);
        if (!await ownerRepository.TryUpdateClaimedAsync(
                handoff.Job,
                handoff.FenceToken,
                cancellationToken))
        {
            throw new InvalidOperationException("Current fence owner could not complete the job.");
        }
    }
}

[CollectionDefinition(nameof(HangfirePostgreSqlColdStartCollection), DisableParallelization = true)]
public sealed class HangfirePostgreSqlColdStartCollection;
