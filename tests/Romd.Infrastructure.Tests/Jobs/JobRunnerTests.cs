using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutorThrows_FailsJobCleanlyWithoutRethrow()
    {
        var job = UploadJob.Create("library.zip");
        var repository = Substitute.For<IJobRepository<UploadJob>>();
        var executor = Substitute.For<IJobExecutor<UploadJob>>();
        var notifier = Substitute.For<IJobNotifier>();
        var options = Substitute.For<IRomdOptions>();
        var failure = new InvalidOperationException("temporary failure");

        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);
        executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(Task.FromException(failure));
        options.DataDirectory.Returns(Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddScoped<JobAuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());

        await using var provider = services.BuildServiceProvider();
        var runner = new JobRunner<UploadJob>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            executor,
            notifier,
            options,
            NullLogger<JobRunner<UploadJob>>.Instance);

        // The runner records a clean terminal failure (with the real error) instead of rethrowing
        // into a Hangfire retry that can't resume the disposed workspace.
        await Should.NotThrowAsync(
            () => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

        job.IsTerminal.ShouldBeTrue();
        job.Phase.ShouldBe(UploadPhase.Failed.ToString());
        job.Errors.ShouldContain(error => error.Message.Contains("temporary failure"));
        // One checkpoint after Start, one after the failure is recorded.
        await repository.Received(2).UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OutboxNotifierFails_CompletesJob()
    {
        await using var connection = PostgreSqlTestDatabase.Create();
        var dbOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;
        await using var db = new RomdDbContext(dbOptions);
        var job = UploadJob.Create("library.zip");
        var repository = Substitute.For<IJobRepository<UploadJob>>();
        var executor = Substitute.For<IJobExecutor<UploadJob>>();
        var notifier = new OutboxJobNotifier(
            new ThrowingAdminRealtimeOutbox(),
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxJobNotifier>.Instance);
        var options = Substitute.For<IRomdOptions>();

        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);
        executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(_ =>
            {
                job.BeginClassification();
                job.SetDiscoveredFiles(dats: 0, roms: 0);
                job.BeginDatIngestion();
                job.BeginRomIngestion();
                return Task.CompletedTask;
            });
        options.DataDirectory.Returns(Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddScoped<JobAuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());

        await using var provider = services.BuildServiceProvider();
        var runner = new JobRunner<UploadJob>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            executor,
            notifier,
            options,
            NullLogger<JobRunner<UploadJob>>.Instance);

        await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

        job.IsTerminal.ShouldBeTrue();
        job.Phase.ShouldBe(UploadPhase.Completed.ToString());
        await repository.Received(2).UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ResumableJobMidPhase_ResumesWithoutStartTransitionAndCompletes()
    {
        // A redelivered resumable job must not re-run the Pending transition: no
        // phase-transition exception path may mark a recovered job Failed.
        var job = ReplaceDatJob.Create(7, "replacement.dat");
        job.Start("hangfire-0");
        job.SetNewDatId(42);
        job.BeginReplacing();
        var executor = Substitute.For<IResumableJobExecutor<ReplaceDatJob>>();
        var (runner, repository) = NewResumableRunner(job, executor, Path.GetTempPath());

        await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

        job.Phase.ShouldBe(ReplaceDatPhase.Completed.ToString());
        job.HangfireJobId.ShouldBe("hangfire-1");
        job.Errors.ShouldBeEmpty();
        await executor.Received(1).ExecuteAsync(job, Arg.Any<JobContext>());
        await repository.Received(2).UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ResumableExecutorThrows_RethrowsPreservesPhaseAndKeepsWorkspace()
    {
        var job = ReplaceDatJob.Create(7, "replacement.dat");
        var executor = Substitute.For<IResumableJobExecutor<ReplaceDatJob>>();
        executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(Task.FromException(new InvalidOperationException("transient failure")));
        string dataDirectory = NewTempDataDirectory();
        string workspacePath = CreateWorkspace(dataDirectory, job.Id);
        var (runner, repository) = NewResumableRunner(job, executor, dataDirectory);

        try
        {
            // The failure is rethrown for redelivery; the job keeps its resumable phase
            // and its workspace instead of being terminally failed.
            await Should.ThrowAsync<InvalidOperationException>(
                () => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

            job.IsTerminal.ShouldBeFalse();
            job.Phase.ShouldBe(ReplaceDatPhase.Ingesting.ToString());
            // The attempt's cause is recorded on the still-non-terminal job before the rethrow.
            job.Errors.ShouldContain(error => error.Item == "attempt" && error.Message == "transient failure");
            Directory.Exists(workspacePath).ShouldBeTrue();
            await repository.Received(2).UpdateAsync(job, Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ResumableExecutorSucceeds_DisposesWorkspace()
    {
        var job = ReplaceDatJob.Create(7, "replacement.dat");
        var executor = Substitute.For<IResumableJobExecutor<ReplaceDatJob>>();
        executor.ExecuteAsync(job, Arg.Any<JobContext>())
            .Returns(_ =>
            {
                job.SetNewDatId(42);
                job.BeginReplacing();
                return Task.CompletedTask;
            });
        string dataDirectory = NewTempDataDirectory();
        string workspacePath = CreateWorkspace(dataDirectory, job.Id);
        var (runner, _) = NewResumableRunner(job, executor, dataDirectory);

        try
        {
            await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

            job.Phase.ShouldBe(ReplaceDatPhase.Completed.ToString());
            Directory.Exists(workspacePath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ResumableTerminalJob_NoOpsWithoutExecutorOrCheckpoints()
    {
        var job = ReplaceDatJob.Create(7, "replacement.dat");
        job.Fail("previously failed");
        var executor = Substitute.For<IResumableJobExecutor<ReplaceDatJob>>();
        var (runner, repository) = NewResumableRunner(job, executor, Path.GetTempPath());

        await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!);
        await repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task RunAsync_MoveImport_TerminalSaveFails_NoSourcesDeleted()
    {
        // The persistence save of the Completed phase fails: the durable state never reads
        // Completed, so move-mode cleanup must not delete any originals.
        var fixture = new MoveImportFixture();
        try
        {
            var (runner, repository, job) = await fixture.CreateAsync();
            int saves = 0;
            repository.UpdateAsync(job, Arg.Any<CancellationToken>()).Returns(_ =>
                ++saves >= 2
                    ? Task.FromException(new InvalidOperationException("db unavailable"))
                    : Task.CompletedTask);

            try
            {
                await runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The failed terminal save propagates; the invariant under test is below.
            }

            File.Exists(fixture.SourceFile).ShouldBeTrue(
                "the Completed phase was never persisted, so no source may be deleted");
            File.Exists(fixture.ManifestPath).ShouldBeTrue();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RunAsync_MoveImport_PersistedCompleted_DeletesDurablyStoredSources()
    {
        var fixture = new MoveImportFixture();
        try
        {
            var (runner, _, job) = await fixture.CreateAsync();

            await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

            job.Phase.ShouldBe(UploadPhase.Completed.ToString());
            File.Exists(fixture.SourceFile).ShouldBeFalse();
            File.Exists(fixture.ManifestPath).ShouldBeFalse();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RunAsync_MoveImport_CompletedWithErrors_RetainsSourcesAndManifest()
    {
        var fixture = new MoveImportFixture();
        try
        {
            var (runner, _, job) = await fixture.CreateAsync(recordError: true);

            await Should.NotThrowAsync(() => runner.RunAsync(job.Id, "hangfire-1", CancellationToken.None));

            job.Phase.ShouldBe(UploadPhase.CompletedWithErrors.ToString());
            File.Exists(fixture.SourceFile).ShouldBeTrue();
            // The orphaned-job sweep resolves the manifest for non-Completed terminal jobs.
            File.Exists(fixture.ManifestPath).ShouldBeTrue();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     A move-mode path import wired through the real ImportSourceCleanup: a staged source
    ///     file under an allowed root, a v2 manifest with the staged fingerprint, and a JobItem
    ///     that proves durable storage — so cleanup WOULD delete unless a safety gate stops it.
    /// </summary>
    private sealed class MoveImportFixture : IDisposable
    {
        private readonly string _tempRoot;

        public MoveImportFixture()
        {
            _tempRoot = Directory.CreateTempSubdirectory("romd-jobrunner-move-").FullName;
            DataDirectory = Path.Combine(_tempRoot, "data");
            AllowedRoot = Path.Combine(_tempRoot, "allowed");
            SourceFile = Path.Combine(AllowedRoot, "source", "game.rom");
            Directory.CreateDirectory(Path.Combine(DataDirectory, "temp", "jobs"));
            Directory.CreateDirectory(Path.GetDirectoryName(SourceFile)!);
        }

        public string DataDirectory { get; }
        public string AllowedRoot { get; }
        public string SourceFile { get; }
        public string ManifestPath { get; private set; } = null!;

        public async Task<(JobRunner<UploadJob> Runner, IJobRepository<UploadJob> Repository, UploadJob Job)>
            CreateAsync(bool recordError = false)
        {
            var job = UploadJob.Create("source/", null, null);
            job.SetImportSource(Path.Combine(AllowedRoot, "source"), move: true);
            if (recordError)
            {
                job.RecordError("game.rom", "simulated error");
            }

            await File.WriteAllTextAsync(SourceFile, "content");
            var info = new FileInfo(SourceFile);
            ManifestPath = ImportManifest.PathFor(DataDirectory, job.Id);
            await ImportManifest.WriteAsync(
                ManifestPath,
                new ImportManifest(
                    ImportManifest.CurrentVersion,
                    AllowedRoot,
                    "source",
                    true,
                    [new ImportManifestEntry(
                        "source/game.rom", "game.rom", info.Length, info.LastWriteTimeUtc)]),
                CancellationToken.None);

            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(DataDirectory);
            options.AllowedImportPaths.Returns([AllowedRoot]);

            var repository = Substitute.For<IJobRepository<UploadJob>>();
            repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

            var jobItems = Substitute.For<IJobItemRepository>();
            jobItems.GetAllByJobAsync(job.Id, Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<JobItemView>>([
                    new JobItemView
                    {
                        Id = Guid.NewGuid(),
                        JobId = job.Id,
                        Kind = JobItemKind.Rom,
                        FileName = "game.rom",
                        SizeBytes = 1,
                        Outcome = JobItemOutcome.Ingested,
                        MatchedTitles = [],
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]);

            var executor = Substitute.For<IJobExecutor<UploadJob>>();
            executor.ExecuteAsync(job, Arg.Any<JobContext>()).Returns(_ =>
            {
                job.BeginClassification();
                job.SetDiscoveredFiles(0, 1);
                job.BeginDatIngestion();
                job.BeginRomIngestion();
                job.RecordRomResult(RomIngestOutcome.Ingested);
                return Task.CompletedTask;
            });

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<JobAuditContext>();
            services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
            services.AddScoped(_ => new ImportSourceCleanup(
                options,
                new PathValidator(options),
                jobItems,
                NullLogger<ImportSourceCleanup>.Instance));
            var provider = services.BuildServiceProvider();

            var runner = new JobRunner<UploadJob>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                executor,
                Substitute.For<IJobNotifier>(),
                options,
                NullLogger<JobRunner<UploadJob>>.Instance);

            return (runner, repository, job);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempRoot, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static (JobRunner<ReplaceDatJob> Runner, IJobRepository<ReplaceDatJob> Repository) NewResumableRunner(
        ReplaceDatJob job,
        IJobExecutor<ReplaceDatJob> executor,
        string dataDirectory)
    {
        var repository = Substitute.For<IJobRepository<ReplaceDatJob>>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(dataDirectory);

        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddScoped<JobAuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>());
        var provider = services.BuildServiceProvider();

        var runner = new JobRunner<ReplaceDatJob>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            executor,
            Substitute.For<IJobNotifier>(),
            options,
            NullLogger<JobRunner<ReplaceDatJob>>.Instance);

        return (runner, repository);
    }

    private static string NewTempDataDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"romd-jobrunner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateWorkspace(string dataDirectory, Guid jobId)
    {
        string workspacePath = Path.Combine(dataDirectory, "temp", "jobs", jobId.ToString("N"));
        Directory.CreateDirectory(workspacePath);
        File.WriteAllText(Path.Combine(workspacePath, "replacement.dat"), "<datafile/>");
        return workspacePath;
    }

    private sealed class ThrowingAdminRealtimeOutbox : IAdminEventOutbox
    {
        public Task EnqueueAsync(string eventType, CancellationToken ct = default)
            => throw new InvalidOperationException("outbox unavailable");

        public Task EnqueueAsync<TPayload>(string eventType, TPayload payload, CancellationToken ct = default)
            => throw new InvalidOperationException("outbox unavailable");

    }
}
