using System.IO.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathImportAcceptanceTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly IDriveInfo _driveInfo = Substitute.For<IDriveInfo>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly IUploadJobAcceptance _acceptance = Substitute.For<IUploadJobAcceptance>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly string _sourceRoot;
    private readonly string _tempRoot;

    public PathImportAcceptanceTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-pathimport-").FullName;
        _dataDirectory = Path.Combine(_tempRoot, "data");
        _sourceRoot = Path.Combine(_tempRoot, "allowed", "roms-folder");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_sourceRoot);

        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _options.DataDirectory.Returns(_dataDirectory);
        _options.AllowedImportPaths.Returns([Path.Combine(_tempRoot, "allowed")]);
        _driveInfo.AvailableFreeSpace.Returns(long.MaxValue);
        _fileSystem.DriveInfo.New(Arg.Any<string>()).Returns(_driveInfo);
        _fileSystem.File.Returns(new FileSystem().File);
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

    private ImportFromPathCommandHandler CreateCreator() => new(
        Substitute.For<ITrackedTitleReader>(), Substitute.For<IReferenceCatalogService>(), Substitute.For<ICurrentUser>(),
        Preparation(), new JobWorkspaceExclusion(_options), _acceptance, _work, TimeProvider.System,
        NullLogger<ImportFromPathCommandHandler>.Instance);

    private string WorkspacePathFor(Guid jobId) =>
        Path.Combine(_dataDirectory, "temp", "jobs", jobId.ToString("N"));

    private string ManifestPathFor(Guid jobId) =>
        ImportManifest.PathFor(_dataDirectory, jobId);

    private PathImportPreparation Preparation(IPathValidator? validator = null) => new(
        _options, validator ?? new PathValidator(_options), _fileSystem, TimeProvider.System,
        NullLogger<PathImportPreparation>.Instance);

    [Fact]
    public async Task PrepareAsync_InspectionFreezesSelectionAndRetainsCompletedMoveInputsOnRelease()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "first.rom"), "first");
        var preparation = Preparation();
        var plan = preparation.Inspect(_sourceRoot).Value;
        plan.Facts.FileCount.ShouldBe(1);
        plan.Facts.SourceRoot.ShouldBe(_sourceRoot);
        plan.Facts.SourceFilename.ShouldBe("roms-folder/");
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "later.rom"), "not observed");
        var id = Guid.NewGuid();
        var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);

        var result = await preparation.PrepareAsync(plan, id, move: true, lease);

        result.Value.Workspace.JobId.ShouldBe(id);
        result.Value.CopyIssues.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(WorkspacePathFor(id), "first.rom"))).ShouldBe("first");
        File.Exists(Path.Combine(WorkspacePathFor(id), "later.rom")).ShouldBeFalse();
        var manifest = (await ImportManifest.ReadAsync(ManifestPathFor(id), default)).ShouldNotBeNull();
        manifest.Entries.ShouldHaveSingleItem().WorkspaceRelativePath.ShouldBe("first.rom");
        await lease.DisposeAsync();
        File.Exists(ManifestPathFor(id)).ShouldBeTrue();
        File.Exists(Path.Combine(_sourceRoot, "first.rom")).ShouldBeTrue();
        await _acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task DiscardAsync_PreparedMoveRemovesOnlyWorkspaceAndManifest()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.rom"), "source stays");
        var preparation = Preparation();
        var plan = preparation.Inspect(_sourceRoot).Value;
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        var prepared = (await preparation.PrepareAsync(plan, id, move: true, lease)).Value;

        await prepared.Workspace.DiscardAsync();

        Directory.Exists(WorkspacePathFor(id)).ShouldBeFalse();
        File.Exists(ManifestPathFor(id)).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_sourceRoot, "game.rom"))).ShouldBe("source stays");
    }

    [Fact]
    public async Task PrepareAsync_FileRejectedAfterInspectionIsNeverOpenedOrAccepted()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.rom"), "unchanged source");
        var validator = Substitute.For<IPathValidator>();
        validator.ValidateImportRoot(Arg.Any<string>()).Returns(Path.Combine(_tempRoot, "allowed"));
        bool rejected = false;
        validator.ValidateImportFile(Arg.Any<string>()).Returns(_ => rejected
            ? ErrorOr.Error.Validation("Import.UnsafePath", "Source changed after inspection.")
            : ErrorOr.Result.Success);
        var preparation = Preparation(validator);
        var plan = preparation.Inspect(_sourceRoot).Value;
        rejected = true;
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);

        var result = await preparation.PrepareAsync(plan, id, move: true, lease);

        result.FirstError.ShouldBe(ImportErrors.NoFilesFound(_sourceRoot));
        Directory.Exists(WorkspacePathFor(id)).ShouldBeFalse();
        File.Exists(ManifestPathFor(id)).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_sourceRoot, "game.rom"))).ShouldBe("unchanged source");
        await _acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    private async Task<UploadJob> CapturePersistedJobAsync(
        Func<Task> act)
    {
        UploadJob? persisted = null;
        await _acceptance.AddStagedAsync(Arg.Do<UploadJob>(job => persisted = job), Arg.Any<CancellationToken>());
        await act();
        persisted.ShouldNotBeNull();
        return persisted;
    }

    [Fact]
    public async Task Handle_FeatureDisabled_ReturnsFeatureDisabledWithoutStaging()
    {
        _options.AllowedImportPaths.Returns([]);
        var creator = CreateCreator();

        var result = await creator.HandleAsync(new(_sourceRoot, Move: false));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.FeatureDisabled");
        Directory.Exists(Path.Combine(_dataDirectory, "temp")).ShouldBeFalse();
        await _acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_EmptyDirectory_ReturnsNoFilesFound()
    {
        var creator = CreateCreator();

        var result = await creator.HandleAsync(new(_sourceRoot, Move: false));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.NoFilesFound");
    }

    [Fact]
    public async Task Handle_InsufficientSpace_ReturnsErrorNamingBothNumbers()
    {
        File.WriteAllBytes(Path.Combine(_sourceRoot, "game.rom"), new byte[128]);
        _driveInfo.AvailableFreeSpace.Returns(512);
        var creator = CreateCreator();

        var result = await creator.HandleAsync(new(_sourceRoot, Move: false));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.InsufficientSpace");
        result.FirstError.Description.ShouldContain("512");
        result.FirstError.Description.ShouldContain((128 + ImportPreflight.MinimumHeadroomBytes).ToString("N0"));
        await _acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
    }

    [Fact]
    public async Task Handle_PopulatesWorkspacePreservingRelativePaths()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "top.rom"), "top");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "nested", "deep"));
        File.WriteAllText(Path.Combine(_sourceRoot, "nested", "deep", "leaf.rom"), "leaf");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.HandleAsync(new(_sourceRoot, Move: false));
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
    public async Task Handle_BadFileIsIsolated_GoodFileStillImported()
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
            var result = await creator.HandleAsync(new(_sourceRoot, Move: false));
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        File.Exists(Path.Combine(workspace, "good.rom")).ShouldBeTrue();
        File.Exists(Path.Combine(workspace, "bad.rom")).ShouldBeFalse();
        job.Errors.ShouldContain(error => error.Message.Contains("Import path contains a symbolic link"));
    }

    [Fact]
    public async Task Handle_MoveTrue_WritesRootRelativeManifestWithFingerprintsOutsideWorkspace()
    {
        string sourceFile = Path.Combine(_sourceRoot, "game.rom");
        File.WriteAllText(sourceFile, "content");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "nested"));
        string nestedFile = Path.Combine(_sourceRoot, "nested", "deep.rom");
        File.WriteAllText(nestedFile, "nested-content");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.HandleAsync(new(_sourceRoot, Move: true));
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
    public async Task Handle_MoveFalse_WritesNoManifest()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "game.rom"), "content");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.HandleAsync(new(_sourceRoot, Move: false));
            result.IsError.ShouldBeFalse();
        });

        File.Exists(ManifestPathFor(job.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_StageFailsBeforeCommitAttempt_CleansWorkspaceAndManifestWithoutMutatingSource()
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "game.rom"), "content");
        UploadJob? persisted = null;
        _acceptance.AddStagedAsync(Arg.Do<UploadJob>(job => persisted = job), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("application transaction failed"));
        var creator = CreateCreator();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            creator.HandleAsync(new(_sourceRoot, Move: true)));

        persisted.ShouldNotBeNull();
        Directory.Exists(WorkspacePathFor(persisted.Id)).ShouldBeFalse();
        File.Exists(ManifestPathFor(persisted.Id)).ShouldBeFalse();
        persisted.Phase.ShouldBe(nameof(UploadPhase.Pending));
        File.Exists(Path.Combine(_sourceRoot, "game.rom")).ShouldBeTrue();
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
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
            var result = await creator.HandleAsync(new(_sourceRoot, Move: true));
            result.IsError.ShouldBeFalse();
        });

        string workspace = WorkspacePathFor(job.Id);
        Directory.Delete(workspace, true);

        File.Exists(sourceFile).ShouldBeTrue();
        File.Exists(ManifestPathFor(job.Id)).ShouldBeTrue();
    }
}
