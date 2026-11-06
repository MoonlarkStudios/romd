using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Realtime;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobCheckpointNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_RealNotifierPayloadQueryFails_PreservesCommittedStateAndRunsCompletionCleanup(bool checkpointDuringExecution)
    {
        await using var rig = await Rig.CreateAsync(checkpointDuringExecution, cancel: false);

        await Should.NotThrowAsync(rig.RunAsync);

        rig.Query.Failures.ShouldBe(checkpointDuringExecution ? 2 : 1);
        rig.Logger.Failures.Count.ShouldBe(rig.Query.Failures);
        rig.Logger.Failures.ShouldAllBe(error => ReferenceEquals(error, rig.Query.Failure));
        rig.Cleanup.Calls.ShouldBe(1);
        Directory.Exists(rig.Workspace).ShouldBeFalse();
        await using var read = rig.Database.CreateContext();
        var job = (await new UploadJobRepository(read, TimeProvider.System).GetByIdAsync(rig.JobId)).ShouldNotBeNull();
        job.PhaseEnum.ShouldBe(UploadPhase.Completed);
        job.CompletedAt.ShouldNotBeNull();
        job.Errors.ShouldBeEmpty();
        job.RomsProcessed.ShouldBe(0);
        job.DatsProcessed.ShouldBe(0);
        (await read.Jobs.SingleAsync()).ExecutionFenceToken.ShouldBeNull();
    }

    [Fact]
    public async Task Checkpoint_RequestedShutdownDuringNotifierQuery_RethrowsOriginalCancellationAfterDurableCheckpoint()
    {
        await using var rig = await Rig.CreateAsync(checkpointDuringExecution: true, cancel: true);
        OperationCanceledException? observed = null;
        try
        {
            await rig.RunAsync();
        }
        catch (OperationCanceledException error)
        {
            observed = error;
        }

        observed.ShouldBeSameAs(rig.Query.Failure);
        observed.ShouldNotBeNull().CancellationToken.ShouldBe(rig.Shutdown.Token);
        rig.Query.Failures.ShouldBe(1);
        rig.Cleanup.Calls.ShouldBe(0);
        Directory.Exists(rig.Workspace).ShouldBeTrue();
        await using var read = rig.Database.CreateContext();
        var job = (await new UploadJobRepository(read, TimeProvider.System).GetByIdAsync(rig.JobId)).ShouldNotBeNull();
        job.PhaseEnum.ShouldBe(UploadPhase.IngestingRoms);
        job.CompletedAt.ShouldBeNull();
        job.Errors.ShouldBeEmpty();
        (await read.Jobs.SingleAsync()).ExecutionFenceToken.ShouldNotBeNull();
    }

    private sealed class Rig(PostgreSqlTestDatabase database, ServiceProvider services, RomdDbContext notifierContext,
        Guid jobId, string root, CancellationTokenSource shutdown, SummaryQueryFailure query, CompletionProbe cleanup,
        FailureLogger<JobRunner<UploadJob>> logger, JobRunner<UploadJob> runner) : IAsyncDisposable
    {
        public PostgreSqlTestDatabase Database => database;
        public Guid JobId => jobId;
        public string Workspace => Path.Combine(root, "temp", "jobs", jobId.ToString("N"));
        public CancellationTokenSource Shutdown => shutdown;
        public SummaryQueryFailure Query => query;
        public CompletionProbe Cleanup => cleanup;
        public FailureLogger<JobRunner<UploadJob>> Logger => logger;
        public Task RunAsync() => runner.RunAsync(jobId, "checkpoint-notification", shutdown.Token);

        public static async Task<Rig> CreateAsync(bool checkpointDuringExecution, bool cancel)
        {
            var database = PostgreSqlTestDatabase.Create();
            var job = UploadJob.Create("fixture.zip", DateTimeOffset.UtcNow);
            await using (var seed = database.CreateContext())
            {
                await using var acceptance = await new EfUnitOfWork(seed).BeginTransactionAsync();
                await new UploadJobRepository(seed, TimeProvider.System).AddStagedAsync(job);
                await acceptance.CommitAsync();
            }
            string root = Path.Combine(Path.GetTempPath(), $"romd-checkpoint-notification-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "temp", "jobs", job.Id.ToString("N")));
            var shutdown = new CancellationTokenSource();
            var query = new SummaryQueryFailure(shutdown, cancel);
            var notifierContext = new RomdDbContext(database.CreateOptions(builder => builder.AddInterceptors(query)));
            var notifier = new OutboxJobNotifier(new AdminRealtimeOutbox(notifierContext, TimeProvider.System), notifierContext,
                new AdminRealtimeOutboxNotifierCommitGate(), NullLogger<OutboxJobNotifier>.Instance);
            var cleanup = new CompletionProbe(database);
            var services = new ServiceCollection()
                .AddScoped(_ => database.CreateContext())
                .AddSingleton(TimeProvider.System)
                .AddSingleton(new JobExecutionClaimOptions())
                .AddScoped<UploadJobRepository>()
                .AddScoped<IJobRepository<UploadJob>, ClaimedJobRepository<UploadJob, UploadJobRepository>>()
                .AddScoped<IAuditContext, JobAuditContext>()
                .AddRomdJobLifecyclePolicies()
                .AddSingleton<IJobCompletionCleanup<UploadJob>>(cleanup)
                .BuildServiceProvider();
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(root);
            var logger = new FailureLogger<JobRunner<UploadJob>>();
            var executor = new CheckpointExecutor(database, query, checkpointDuringExecution);
            var runner = new JobRunner<UploadJob>(services.GetRequiredService<IServiceScopeFactory>(), executor, notifier,
                options, logger);
            return new(database, services, notifierContext, job.Id, root, shutdown, query, cleanup, logger, runner);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await notifierContext.DisposeAsync();
            shutdown.Dispose();
            database.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CheckpointExecutor(PostgreSqlTestDatabase database, SummaryQueryFailure query,
        bool checkpointDuringExecution) : IJobExecutor<UploadJob>
    {
        public async Task ExecuteAsync(UploadJob job, JobContext context)
        {
            job.BeginClassification();
            job.SetDiscoveredFiles(0, 0);
            job.BeginDatIngestion();
            job.BeginRomIngestion();
            query.Enabled = true;
            if (checkpointDuringExecution)
            {
                await context.CheckpointAsync(context.CancellationToken);
                await using var read = database.CreateContext();
                (await read.Jobs.SingleAsync()).Phase.ShouldBe(nameof(UploadPhase.IngestingRoms));
            }
        }
    }

    private sealed class SummaryQueryFailure(CancellationTokenSource shutdown, bool cancel) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Failures { get; private set; }
        public Exception Failure { get; } = cancel
            ? new OperationCanceledException("notifier payload query cancelled", shutdown.Token)
            : new IOException("notifier payload query failed before its outbox catch");

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("\"Platforms\"", StringComparison.Ordinal))
            {
                Failures++;
                if (cancel)
                    shutdown.Cancel();
                throw Failure;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CompletionProbe(PostgreSqlTestDatabase database) : IJobCompletionCleanup<UploadJob>
    {
        public int Calls { get; private set; }
        public async Task RunAfterCompletionPersistedAsync(UploadJob job, CancellationToken ct = default)
        {
            await using var read = database.CreateContext();
            (await read.Jobs.SingleAsync(ct)).Phase.ShouldBe(nameof(UploadPhase.Completed));
            Calls++;
        }
    }

    private sealed class FailureLogger<T> : ILogger<T>
    {
        public List<Exception> Failures { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Failures.Add(exception);
        }
    }
}
