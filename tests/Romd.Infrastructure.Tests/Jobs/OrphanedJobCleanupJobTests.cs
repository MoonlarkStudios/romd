using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;
using HangfireJob = Hangfire.Common.Job;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class OrphanedJobCleanupJobTests : IDisposable
{
    private readonly string _allowedRoot;
    private readonly string _dataDirectory;
    private readonly IJobItemRepository _jobItems = Substitute.For<IJobItemRepository>();
    private readonly IUploadJobRepository _jobRepo = Substitute.For<IUploadJobRepository>();
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly string _sourceRoot;
    private readonly string _tempRoot;
    private readonly MutableTimeProvider _timeProvider = new();

    public OrphanedJobCleanupJobTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-orphancleanup-").FullName;
        _dataDirectory = Path.Combine(_tempRoot, "data");
        _allowedRoot = Path.Combine(_tempRoot, "allowed");
        _sourceRoot = Path.Combine(_allowedRoot, "source");
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "temp", "jobs"));
        Directory.CreateDirectory(_sourceRoot);
        _options.DataDirectory.Returns(_dataDirectory);
        _options.AllowedImportPaths.Returns([_allowedRoot]);
        _jobRepo.FailStaleJobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(0);
        _jobItems.GetAllByJobAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<JobItemView>>([]);
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

    private OrphanedJobCleanupJob CreateSweep()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_jobRepo);
        services.AddScoped(_ => new ImportSourceCleanup(
            _options, new PathValidator(_options), _jobItems, NullLogger<ImportSourceCleanup>.Instance));
        var provider = services.BuildServiceProvider();

        return new OrphanedJobCleanupJob(
            _options, provider, _timeProvider, NullLogger<OrphanedJobCleanupJob>.Instance);
    }

    private static PerformContext CreatePerformContext()
    {
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);
        return new PerformContext(
            Substitute.For<JobStorage>(),
            Substitute.For<IStorageConnection>(),
            new BackgroundJob(
                "1",
                HangfireJob.FromExpression<OrphanedJobCleanupJob>(job => job.ExecuteAsync(null!)),
                DateTime.UtcNow),
            cancellationToken);
    }

    private UploadJob StubJob(Guid jobId, UploadPhase phase, bool move = true)
    {
        var job = UploadJob.Rehydrate(
            jobId, Guid.NewGuid(), "source/", null, phase, null,
            datsDiscovered: 0, romsDiscovered: 0, datsProcessed: 0, datsSucceeded: 0,
            romsProcessed: 0, romsIngested: 0, romsDeduplicated: 0, romsRejected: 0,
            maxParallelRoms: 4, allowUnidentified: false, archiveOnly: false,
            importSourcePath: _sourceRoot, importMove: move, currentItem: null, errors: [],
            createdAt: DateTimeOffset.UtcNow, startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow, isArchived: false, archivedAt: null,
            createdByUserId: null);
        _jobRepo.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);
        return job;
    }

    private async Task<(string SourceFile, string ManifestPath)> StageManifestAsync(Guid jobId)
    {
        string sourceFile = Path.Combine(_sourceRoot, "game.rom");
        await File.WriteAllTextAsync(sourceFile, "content");
        var info = new FileInfo(sourceFile);
        string manifestPath = ImportManifest.PathFor(_dataDirectory, jobId);
        await ImportManifest.WriteAsync(
            manifestPath,
            new ImportManifest(
                ImportManifest.CurrentVersion, _allowedRoot, "source", true,
                [new ImportManifestEntry("source/game.rom", "game.rom", info.Length, info.LastWriteTimeUtc)]),
            CancellationToken.None);
        return (sourceFile, manifestPath);
    }

    private void SetIngestedJobItem(Guid jobId)
    {
        _jobItems.GetAllByJobAsync(jobId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<JobItemView>>([
                new JobItemView
                {
                    Id = Guid.NewGuid(),
                    JobId = jobId,
                    Kind = JobItemKind.Rom,
                    FileName = "game.rom",
                    SizeBytes = 1,
                    Outcome = JobItemOutcome.Ingested,
                    MatchedTitles = [],
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]);
    }

    [Fact]
    public async Task Execute_ManifestForPersistedCompletedJob_RunsCleanupOnSweep()
    {
        // Durable backstop: a crash after the Completed save but before cleanup leaves the
        // manifest behind; the sweep must finish the job's move by re-reading persisted state.
        Guid jobId = Guid.NewGuid();
        var (sourceFile, manifestPath) = await StageManifestAsync(jobId);
        StubJob(jobId, UploadPhase.Completed);
        SetIngestedJobItem(jobId);

        await CreateSweep().ExecuteAsync(CreatePerformContext());

        File.Exists(sourceFile).ShouldBeFalse();
        File.Exists(manifestPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(UploadPhase.CompletedWithErrors)]
    [InlineData(UploadPhase.Failed)]
    [InlineData(UploadPhase.Cancelled)]
    public async Task Execute_ManifestForOtherTerminalJob_DeletesManifestAndRetainsSources(UploadPhase phase)
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, manifestPath) = await StageManifestAsync(jobId);
        StubJob(jobId, phase);
        SetIngestedJobItem(jobId);

        await CreateSweep().ExecuteAsync(CreatePerformContext());

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_ManifestForNonTerminalJob_IsLeftUntilAgeOut()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, manifestPath) = await StageManifestAsync(jobId);
        StubJob(jobId, UploadPhase.IngestingRoms);
        SetIngestedJobItem(jobId);

        await CreateSweep().ExecuteAsync(CreatePerformContext());

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Execute_StaleManifestForNonTerminalJob_IsAgedOutRetainingSources()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, manifestPath) = await StageManifestAsync(jobId);
        StubJob(jobId, UploadPhase.IngestingRoms);
        _timeProvider.UtcNow = DateTimeOffset.UtcNow.AddHours(5);

        await CreateSweep().ExecuteAsync(CreatePerformContext());

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_CompletedJobWithSourceNotVisible_KeepsManifestThenAgesOut()
    {
        // Split-host semantics: sources invisible from this host are retried per sweep and the
        // manifest is only discarded by the bounded age-out.
        Guid jobId = Guid.NewGuid();
        var (sourceFile, manifestPath) = await StageManifestAsync(jobId);
        File.Delete(sourceFile);
        StubJob(jobId, UploadPhase.Completed);
        SetIngestedJobItem(jobId);
        var sweep = CreateSweep();

        await sweep.ExecuteAsync(CreatePerformContext());
        File.Exists(manifestPath).ShouldBeTrue();

        _timeProvider.UtcNow = DateTimeOffset.UtcNow.AddHours(5);
        await sweep.ExecuteAsync(CreatePerformContext());
        File.Exists(manifestPath).ShouldBeFalse();
    }


    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
