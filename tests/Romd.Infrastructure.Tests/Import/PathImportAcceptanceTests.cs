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
        _options, validator ?? new PathValidator(_options), _fileSystem);

    [Fact]
    public async Task ReserveAsync_CreatesOnlyAnEmptyWorkspaceUnderTheLease()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.rom"), "source stays");
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);

        var reserved = await Preparation().ReserveAsync(id, lease);

        reserved.JobId.ShouldBe(id);
        Directory.EnumerateFileSystemEntries(WorkspacePathFor(id)).ShouldBeEmpty();
        File.Exists(ManifestPathFor(id)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => Preparation().ReserveAsync(id, lease));
    }

    [Fact]
    public async Task DiscardAsync_ReservedWorkspaceRemovesOnlyTheWorkspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.rom"), "source stays");
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        var reserved = await Preparation().ReserveAsync(id, lease);

        await reserved.DiscardAsync();

        Directory.Exists(WorkspacePathFor(id)).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_sourceRoot, "game.rom"))).ShouldBe("source stays");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AcceptsWithoutCopyingBytesOrWritingAManifest(bool move)
    {
        File.WriteAllText(Path.Combine(_sourceRoot, "top.rom"), "top");
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "nested", "deep"));
        File.WriteAllText(Path.Combine(_sourceRoot, "nested", "deep", "leaf.rom"), "leaf");
        var creator = CreateCreator();

        var job = await CapturePersistedJobAsync(async () =>
        {
            var result = await creator.HandleAsync(new(_sourceRoot, Move: move));
            result.IsError.ShouldBeFalse();
        });

        // The worker copies during the Copying phase; acceptance only reserves the workspace.
        Directory.EnumerateFileSystemEntries(WorkspacePathFor(job.Id)).ShouldBeEmpty();
        File.Exists(ManifestPathFor(job.Id)).ShouldBeFalse();
        job.SourceFilename.ShouldBe("roms-folder/");
        job.ImportSourcePath.ShouldBe(_sourceRoot);
        job.ImportMove.ShouldBe(move);
        job.PhaseEnum.ShouldBe(UploadPhase.Pending);
        job.Errors.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(_sourceRoot, "top.rom")).ShouldBe("top");
    }

    [Fact]
    public async Task Handle_StageFailsBeforeCommitAttempt_CleansWorkspaceWithoutMutatingSource()
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
}
