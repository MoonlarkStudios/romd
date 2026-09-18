using System.IO.Abstractions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;
using HangfireJob = Hangfire.Common.Job;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathImportJobCreatorTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly IDriveInfo _driveInfo = Substitute.For<IDriveInfo>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly IUploadJobRepository _jobRepo = Substitute.For<IUploadJobRepository>();
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly string _sourceRoot;
    private readonly string _tempRoot;

    public PathImportJobCreatorTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-pathimport-").FullName;
        _dataDirectory = Path.Combine(_tempRoot, "data");
        _sourceRoot = Path.Combine(_tempRoot, "allowed", "roms-folder");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_sourceRoot);

        _options.DataDirectory.Returns(_dataDirectory);
        _options.AllowedImportPaths.Returns([Path.Combine(_tempRoot, "allowed")]);
        _driveInfo.AvailableFreeSpace.Returns(long.MaxValue);
        _fileSystem.DriveInfo.New(Arg.Any<string>()).Returns(_driveInfo);
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

    private PathImportJobCreator CreateCreator() =>
        new(
            _jobRepo,
            _options,
            new PathValidator(_options),
            _fileSystem,
            NullLogger<PathImportJobCreator>.Instance);

    private string WorkspacePathFor(Guid jobId) =>
        Path.Combine(_dataDirectory, "temp", "jobs", jobId.ToString("N"));

    private string ManifestPathFor(Guid jobId) =>
        ImportManifest.PathFor(_dataDirectory, jobId);

    private async Task<UploadJob> CapturePersistedJobAsync(
        Func<Task> act)
    {
        UploadJob? persisted = null;
        await _jobRepo.AddAsync(Arg.Do<UploadJob>(job => persisted = job), Arg.Any<CancellationToken>());
        await act();
        persisted.ShouldNotBeNull();
        return persisted;
    }

    [Fact]
    public async Task CreateFromPathAsync_FeatureDisabled_ReturnsFeatureDisabledWithoutStaging()
    {
        _options.AllowedImportPaths.Returns([]);
        var creator = CreateCreator();

        var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.FeatureDisabled");
        Directory.Exists(Path.Combine(_dataDirectory, "temp")).ShouldBeFalse();
        await _jobRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task CreateFromPathAsync_EmptyDirectory_ReturnsNoFilesFound()
    {
        var creator = CreateCreator();

        var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.NoFilesFound");
    }

    [Fact]
    public async Task CreateFromPathAsync_InsufficientSpace_ReturnsErrorNamingBothNumbers()
    {
        File.WriteAllBytes(Path.Combine(_sourceRoot, "game.rom"), new byte[128]);
        _driveInfo.AvailableFreeSpace.Returns(512);
        var creator = CreateCreator();

        var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.InsufficientSpace");
        result.FirstError.Description.ShouldContain("512");
        result.FirstError.Description.ShouldContain((128 + ImportPreflight.MinimumHeadroomBytes).ToString("N0"));
        await _jobRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task CreateFromPathAsync_PopulatesWorkspacePreservingRelativePaths()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "top.rom"), "top");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "nested", "deep"));
        File.WriteAllText(Path.Combine(_sourceRoot, "nested", "deep", "leaf.rom"), "leaf");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        File.ReadAllText(Path.Combine(workspace, "top.rom")).ShouldBe("top");
        File.ReadAllText(Path.Combine(workspace, "nested", "deep", "leaf.rom")).ShouldBe("leaf");
        job.SourceFilename.ShouldBe("roms-folder/");
        job.ImportSourcePath.ShouldBe(_sourceRoot);
        job.ImportMove.ShouldBeFalse();
        File.Exists(Path.Combine(_sourceRoot, "top.rom")).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateFromPathAsync_BadFileIsIsolated_GoodFileStillImported()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "good.rom"), "good");
        string linkTarget = Path.Combine(_tempRoot, "outside.rom");
        File.WriteAllText(linkTarget, "outside");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_sourceRoot, "bad.rom"), linkTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        File.Exists(Path.Combine(workspace, "good.rom")).ShouldBeTrue();
        File.Exists(Path.Combine(workspace, "bad.rom")).ShouldBeFalse();
        job.Errors.ShouldContain(error => error.Message.Contains("Import path contains a symbolic link"));
    }

    [Fact]
    public async Task CreateFromPathAsync_MoveTrue_WritesRootRelativeManifestWithFingerprintsOutsideWorkspace()
    {
        string sourceFile = Path.Combine(_sourceRoot, "game.rom");
        File.WriteAllText(sourceFile, "content");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "nested"));
        string nestedFile = Path.Combine(_sourceRoot, "nested", "deep.rom");
        File.WriteAllText(nestedFile, "nested-content");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: true);
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        string manifestPath = ManifestPathFor(job.Id);
        File.Exists(manifestPath).ShouldBeTrue();
        Path.GetDirectoryName(manifestPath).ShouldNotBe(workspace);
        Directory.EnumerateFiles(workspace, "*.import.json", SearchOption.AllDirectories).ShouldBeEmpty();

        var manifest = await ImportManifest.ReadAsync(manifestPath, CancellationToken.None);
        manifest.ShouldNotBeNull();
        manifest.Version.ShouldBe(ImportManifest.CurrentVersion);
        manifest.Move.ShouldBeTrue();
        manifest.AllowedRoot.ShouldBe(Path.Combine(_tempRoot, "allowed"));
        manifest.SourceRoot.ShouldBe("roms-folder");
        manifest.Entries.Count.ShouldBe(2);

        var top = manifest.Entries.Single(entry => entry.WorkspaceRelativePath == "game.rom");
        top.SourceRelativePath.ShouldBe("roms-folder/game.rom");
        top.SizeBytes.ShouldBe(new FileInfo(sourceFile).Length);
        top.LastWriteTimeUtc.ShouldBe(File.GetLastWriteTimeUtc(sourceFile));

        var nested = manifest.Entries.Single(entry => entry.WorkspaceRelativePath == "nested/deep.rom");
        nested.SourceRelativePath.ShouldBe("roms-folder/nested/deep.rom");
        nested.SizeBytes.ShouldBe(new FileInfo(nestedFile).Length);
        nested.LastWriteTimeUtc.ShouldBe(File.GetLastWriteTimeUtc(nestedFile));
        job.ImportMove.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateFromPathAsync_MoveFalse_WritesNoManifest()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "game.rom"), "content");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: false);
            result.IsError.ShouldBeFalse();
        });

        File.Exists(ManifestPathFor(job.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateFromPathAsync_AcceptanceFails_CleansWorkspaceAndManifestWithoutMutatingSource()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "game.rom"), "content");
        UploadJob? persisted = null;
        _jobRepo.AddAsync(Arg.Do<UploadJob>(job => persisted = job), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("application transaction failed"));
        var creator = CreateCreator();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: true));

        persisted.ShouldNotBeNull();
        Directory.Exists(WorkspacePathFor(persisted.Id)).ShouldBeFalse();
        File.Exists(ManifestPathFor(persisted.Id)).ShouldBeFalse();
        persisted.Phase.ShouldBe(nameof(UploadPhase.Pending));
        File.Exists(Path.Combine(_sourceRoot, "game.rom")).ShouldBeTrue();
        await _jobRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task WorkspaceDeletion_NeverTouchesSourceFiles()
    {
        // THE move-mode safety invariant: the workspace holds only copies and the manifest lives
        // outside it, so recursively deleting the workspace can never destroy the only copy.
        string sourceFile = Path.Combine(_sourceRoot, "game.rom");
        File.WriteAllText(sourceFile, "content");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.CreateFromPathAsync(_sourceRoot, new UploadJobOptions(), move: true);
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        Directory.Delete(workspace, true);

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(ManifestPathFor(job.Id)).ShouldBeTrue();
    }
}
