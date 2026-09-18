using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class ImportSourceCleanupTests : IDisposable
{
    private readonly string _allowedRoot;
    private readonly string _dataDirectory;
    private readonly IJobItemRepository _jobItems = Substitute.For<IJobItemRepository>();
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly string _sourceRoot;
    private readonly string _tempRoot;

    public ImportSourceCleanupTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-importcleanup-").FullName;
        _dataDirectory = Path.Combine(_tempRoot, "data");
        _allowedRoot = Path.Combine(_tempRoot, "allowed");
        _sourceRoot = Path.Combine(_allowedRoot, "source");
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "temp", "jobs"));
        Directory.CreateDirectory(_sourceRoot);
        _options.DataDirectory.Returns(_dataDirectory);
        _options.AllowedImportPaths.Returns([_allowedRoot]);
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

    private ImportSourceCleanup CreateCleanup() =>
        new(_options, new PathValidator(_options), _jobItems, NullLogger<ImportSourceCleanup>.Instance);

    private static UploadJob CreateTerminalImportJob(Guid id, UploadPhase phase, string sourcePath, bool move) =>
        UploadJob.Rehydrate(
            id,
            Guid.NewGuid(),
            "source/",
            platformId: null,
            phase,
            hangfireJobId: null,
            datsDiscovered: 0,
            romsDiscovered: 0,
            datsProcessed: 0,
            datsSucceeded: 0,
            romsProcessed: 0,
            romsIngested: 0,
            romsDeduplicated: 0,
            romsRejected: 0,
            maxParallelRoms: 4,
            allowUnidentified: false,
            archiveOnly: false,
            importSourcePath: sourcePath,
            importMove: move,
            currentItem: null,
            errors: [],
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            isArchived: false,
            archivedAt: null,
            createdByUserId: null);

    /// <summary>Writes a source file and returns its manifest entry with the staged fingerprint.</summary>
    private async Task<(string AbsolutePath, ImportManifestEntry Entry)> StageSourceFileAsync(
        string relativePath,
        string content = "content")
    {
        string absolutePath = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, content);
        var info = new FileInfo(absolutePath);
        var entry = new ImportManifestEntry(
            ImportManifest.NormalizeRelativePath(Path.GetRelativePath(_allowedRoot, absolutePath)),
            ImportManifest.NormalizeRelativePath(relativePath),
            info.Length,
            info.LastWriteTimeUtc);
        return (absolutePath, entry);
    }

    private async Task<string> WriteManifestAsync(Guid jobId, params ImportManifestEntry[] entries)
    {
        string manifestPath = ImportManifest.PathFor(_dataDirectory, jobId);
        var manifest = new ImportManifest(
            ImportManifest.CurrentVersion, _allowedRoot, "source", true, entries);
        await ImportManifest.WriteAsync(manifestPath, manifest, CancellationToken.None);
        return manifestPath;
    }

    private void SetJobItems(Guid jobId, params (string WorkspaceRelativePath, JobItemOutcome Outcome)[] items)
    {
        var views = items.Select(item => new JobItemView
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Kind = item.Outcome is JobItemOutcome.DatRouted or JobItemOutcome.DatUnrouted
                ? JobItemKind.Dat
                : JobItemKind.Rom,
            FileName = item.WorkspaceRelativePath,
            SizeBytes = 1,
            Outcome = item.Outcome,
            MatchedTitles = [],
            CreatedAt = DateTimeOffset.UtcNow
        }).ToList();
        _jobItems.GetAllByJobAsync(jobId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<JobItemView>>(views);
    }

    [Fact]
    public async Task RunCompleted_AllRomsRejected_RetainsEveryOriginal()
    {
        // THE discriminating case: allowUnidentified=false rejecting every ROM still ends the job
        // as Completed, but nothing was durably stored, so every original must survive.
        Guid jobId = Guid.NewGuid();
        var (first, firstEntry) = await StageSourceFileAsync("first.rom");
        var (second, secondEntry) = await StageSourceFileAsync("nested/second.rom");
        string manifestPath = await WriteManifestAsync(jobId, firstEntry, secondEntry);
        SetJobItems(
            jobId,
            ("first.rom", JobItemOutcome.Rejected),
            ("nested/second.rom", JobItemOutcome.Rejected));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(first).ShouldBeTrue();
        File.Exists(second).ShouldBeTrue();
        // All entries are resolved (intentionally retained), so the manifest is done.
        File.Exists(manifestPath).ShouldBeFalse();
    }

    [Fact]
    public async Task RunCompleted_MixedBatch_DeletesOnlyDurablyStoredSources()
    {
        Guid jobId = Guid.NewGuid();
        var (ingested, ingestedEntry) = await StageSourceFileAsync("ingested.rom");
        var (deduplicated, deduplicatedEntry) = await StageSourceFileAsync("deduplicated.rom");
        var (dat, datEntry) = await StageSourceFileAsync("catalog.dat");
        var (rejected, rejectedEntry) = await StageSourceFileAsync("rejected.rom");
        var (failed, failedEntry) = await StageSourceFileAsync("failed.rom");
        var (unsupportedArchive, unsupportedEntry) = await StageSourceFileAsync("weird.7z");
        var (extensionless, extensionlessEntry) = await StageSourceFileAsync("extensionless");
        string manifestPath = await WriteManifestAsync(
            jobId, ingestedEntry, deduplicatedEntry, datEntry, rejectedEntry, failedEntry,
            unsupportedEntry, extensionlessEntry);
        // Unsupported archives and unknown/extensionless files produce NO JobItem at all.
        SetJobItems(
            jobId,
            ("ingested.rom", JobItemOutcome.Ingested),
            ("deduplicated.rom", JobItemOutcome.Deduplicated),
            ("catalog.dat", JobItemOutcome.DatRouted),
            ("rejected.rom", JobItemOutcome.Rejected),
            ("failed.rom", JobItemOutcome.Failed));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(ingested).ShouldBeFalse();
        File.Exists(deduplicated).ShouldBeFalse();
        File.Exists(dat).ShouldBeFalse();
        File.Exists(rejected).ShouldBeTrue();
        File.Exists(failed).ShouldBeTrue();
        File.Exists(unsupportedArchive).ShouldBeTrue();
        File.Exists(extensionless).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeFalse();
    }

    [Fact]
    public async Task RunCompleted_ParentSwappedToSymlinkBeforeCleanup_RetainsTarget()
    {
        // Between staging and cleanup an attacker swaps a parent directory for a symlink pointing
        // outside the allowed root. Deleting through it must be refused.
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("sub/game.rom");
        await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("sub/game.rom", JobItemOutcome.Ingested));

        string outside = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outside);
        string victim = Path.Combine(outside, "game.rom");
        File.Copy(sourceFile, victim);
        Directory.Delete(Path.Combine(_sourceRoot, "sub"), true);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_sourceRoot, "sub"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);
        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(victim).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_ContentReplacedAfterStaging_SizeChanged_Retains()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("game.rom", "original");
        await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        await File.WriteAllTextAsync(sourceFile, "replaced with longer content");
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_ContentReplacedAfterStaging_MtimeChanged_Retains()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("game.rom", "original");
        await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        // Same size, different mtime: still a different file than the one that was ingested.
        File.SetLastWriteTimeUtc(sourceFile, entry.LastWriteTimeUtc.AddMinutes(5));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_TamperedManifestEscapingAllowedRoot_Retains()
    {
        Guid jobId = Guid.NewGuid();
        string outside = Path.Combine(_tempRoot, "outside.rom");
        await File.WriteAllTextAsync(outside, "victim");
        var info = new FileInfo(outside);
        var entry = new ImportManifestEntry("../outside.rom", "outside.rom", info.Length, info.LastWriteTimeUtc);
        await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("outside.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_SourceNotVisible_SkipsWithoutCountingAndKeepsManifestForRetry()
    {
        // Split-host semantics: an ingested source that is not visible from this host is never
        // counted deleted; the manifest is kept so the orphaned-job sweep can retry.
        Guid jobId = Guid.NewGuid();
        var (missing, missingEntry) = await StageSourceFileAsync("missing.rom");
        File.Delete(missing);
        var (present, presentEntry) = await StageSourceFileAsync("present.rom");
        string manifestPath = await WriteManifestAsync(jobId, missingEntry, presentEntry);
        SetJobItems(
            jobId,
            ("missing.rom", JobItemOutcome.Ingested),
            ("present.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(present).ShouldBeFalse();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData(UploadPhase.CompletedWithErrors)]
    [InlineData(UploadPhase.Failed)]
    [InlineData(UploadPhase.Cancelled)]
    public async Task RunCompleted_PhaseNotCompleted_TouchesNothing(UploadPhase phase)
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("game.rom");
        string manifestPath = await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, phase, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_MoveFalse_TouchesNothing()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("game.rom");
        string manifestPath = await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: false);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_LegacyV1Manifest_RetainsSourcesAndKeepsManifest()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, _) = await StageSourceFileAsync("game.rom");
        string manifestPath = ImportManifest.PathFor(_dataDirectory, jobId);
        await File.WriteAllTextAsync(
            manifestPath, $$"""{"Move":true,"SourceFiles":["{{sourceFile}}"]}""");
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCompleted_MissingManifest_DoesNotThrow()
    {
        var job = CreateTerminalImportJob(Guid.NewGuid(), UploadPhase.Completed, _sourceRoot, move: true);

        await CreateCleanup().RunCompletedAsync(job, CancellationToken.None);
    }

    [Fact]
    public async Task RunCompleted_RunTwice_IsIdempotent()
    {
        Guid jobId = Guid.NewGuid();
        var (sourceFile, entry) = await StageSourceFileAsync("game.rom");
        string manifestPath = await WriteManifestAsync(jobId, entry);
        SetJobItems(jobId, ("game.rom", JobItemOutcome.Ingested));
        var job = CreateTerminalImportJob(jobId, UploadPhase.Completed, _sourceRoot, move: true);
        var cleanup = CreateCleanup();

        await cleanup.RunCompletedAsync(job, CancellationToken.None);
        await cleanup.RunCompletedAsync(job, CancellationToken.None);

        File.Exists(sourceFile).ShouldBeFalse();
        File.Exists(manifestPath).ShouldBeFalse();
    }
}
