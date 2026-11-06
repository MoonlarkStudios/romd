using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathImportCopyRecoveryTests : IDisposable
{
    private static readonly byte[] Good = [1, 2, 3, 4];
    private static readonly byte[] Bad = [5, 6, 7, 8];
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private readonly string root = Directory.CreateTempSubdirectory("romd-copy-recovery-").FullName;
    private readonly IRomdOptions options = Substitute.For<IRomdOptions>();
    private readonly PathCopyFailureFileSystem files = new();
    private string DataRoot => Path.Combine(root, "data");
    private string SourceRoot => Path.Combine(root, "allowed", "originals");
    private string Workspace(Guid id) => Path.Combine(DataRoot, "temp", "jobs", id.ToString("N"));

    public PathImportCopyRecoveryTests()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllBytes(Path.Combine(SourceRoot, "good.rom"), Good);
        File.WriteAllBytes(Path.Combine(SourceRoot, "bad.rom"), Bad);
        options.DataDirectory.Returns(DataRoot);
        options.AllowedImportPaths.Returns([Path.Combine(root, "allowed")]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_MixedSuccessAndPartialCopyFailure_AdmitsOnlyCompleteFileAndMoveEntry(bool move)
    {
        files.CopyFailures["bad.rom"] = new IOException("source failed after its prefix");
        var id = Guid.NewGuid();
        files.BeforeDelete = _ => AssertLeaseHeld(id);
        var preparation = Preparation();
        var plan = preparation.Inspect(SourceRoot).Value;
        await using var lease = await new JobWorkspaceExclusion(options).AcquireAsync(id);

        var result = await preparation.PrepareAsync(plan, id, move, lease);

        result.IsError.ShouldBeFalse();
        var issue = result.Value.CopyIssues.ShouldHaveSingleItem();
        issue.Path.ShouldBe(Path.Combine(SourceRoot, "bad.rom"));
        issue.Message.ShouldContain("source failed after its prefix");
        issue.OccurredAt.ShouldBe(Now);
        files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(new byte[] { 5, 6 });
        AssertOnlyGoodWorkspace(id);
        await AssertManifestAsync(id, move);
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Prepare_AllCopiesFail_RemovesStagingAndReturnsNoFilesWithoutTouchingOriginals()
    {
        files.CopyFailures["bad.rom"] = new IOException("bad failed");
        files.CopyFailures["good.rom"] = new UnauthorizedAccessException("good failed after writing");
        var id = Guid.NewGuid();
        files.BeforeDelete = _ => AssertLeaseHeld(id);
        var preparation = Preparation();
        var plan = preparation.Inspect(SourceRoot).Value;
        await using var lease = await new JobWorkspaceExclusion(options).AcquireAsync(id);

        var result = await preparation.PrepareAsync(plan, id, move: true, lease);

        result.FirstError.Code.ShouldBe("Import.NoFilesFound");
        files.DeleteAttempts.Count.ShouldBe(2);
        files.DeleteAttempts.ShouldAllBe(attempt => attempt.Bytes.Length == 2);
        Directory.Exists(Workspace(id)).ShouldBeFalse();
        File.Exists(ImportManifest.PathFor(DataRoot, id)).ShouldBeFalse();
        AssertOriginalsAndDisposedStreams();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PartialTargetCannotBeExcluded_AbortsBeforeAcceptanceAndPreservesCopyAndCleanupCauses(bool silentFailure)
    {
        var primary = new IOException("source failed after its prefix");
        var secondary = new UnauthorizedAccessException("target cannot be deleted");
        files.CopyFailures["bad.rom"] = primary;
        files.DeleteFailure = silentFailure ? null : secondary;
        files.IgnoreDelete = silentFailure;
        Guid? preparedId = null;
        files.BeforeDelete = path =>
        {
            var id = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(path)!), "N");
            preparedId = id;
            AssertLeaseHeld(id);
        };
        var work = Substitute.For<IUnitOfWork>();
        var acceptance = Substitute.For<IUploadJobAcceptance>();
        var handler = Handler(work, acceptance);

        var observed = await CaptureAsync(() => handler.HandleAsync(new(SourceRoot, Move: true)));

        observed.ShouldBeSameAs(primary);
        var cleanup = primary.Data[PathImportFiles.TargetCleanupFailureKey].ShouldBeAssignableTo<Exception>().ShouldNotBeNull();
        if (silentFailure)
            cleanup.ShouldBeOfType<IOException>().Message.ShouldContain("remains");
        else
            cleanup.ShouldBeSameAs(secondary);
        preparedId.HasValue.ShouldBeTrue();
        var rejectedId = preparedId.GetValueOrDefault();
        Directory.Exists(Workspace(rejectedId)).ShouldBeFalse();
        File.Exists(ImportManifest.PathFor(DataRoot, rejectedId)).ShouldBeFalse();
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        using var successor = JobWorkspace.TryAcquireExecutionLock(DataRoot, rejectedId).ShouldNotBeNull();
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Handle_CancellationAfterWritingPrefix_RemovesStagingReleasesLeaseAndPreservesExactCancellationAndToken()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("copy cancelled after its prefix", cancellation.Token);
        files.CopyFailures["bad.rom"] = primary;
        Guid id = default;
        files.BeforeCopyFailure = () =>
        {
            string target = files.Streams.Last(stream => !stream.IsSource).FilePath;
            id = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(target)!), "N");
            AssertLeaseHeld(id);
            cancellation.Cancel();
        };
        var work = Substitute.For<IUnitOfWork>();
        var acceptance = Substitute.For<IUploadJobAcceptance>();

        var observed = await CaptureAsync(() => Handler(work, acceptance).HandleAsync(new(SourceRoot, Move: true), cancellation.Token));

        observed.ShouldBeSameAs(primary);
        observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        id.ShouldNotBe(Guid.Empty);
        Directory.Exists(Workspace(id)).ShouldBeFalse();
        File.Exists(ImportManifest.PathFor(DataRoot, id)).ShouldBeFalse();
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await acceptance.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        using var successor = JobWorkspace.TryAcquireExecutionLock(DataRoot, id).ShouldNotBeNull();
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Prepare_TargetDisposalFails_ExcludesFailedFileFromAcceptedWorkspaceAndMoveManifest()
    {
        files.TargetDisposalFailures["bad.rom"] = new IOException("target flush failed");
        var id = Guid.NewGuid();
        var preparation = Preparation();
        var plan = preparation.Inspect(SourceRoot).Value;
        await using var lease = await new JobWorkspaceExclusion(options).AcquireAsync(id);

        var result = await preparation.PrepareAsync(plan, id, move: true, lease);

        result.IsError.ShouldBeFalse();
        result.Value.CopyIssues.ShouldHaveSingleItem().Message.ShouldContain("target flush failed");
        files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(Bad);
        AssertOnlyGoodWorkspace(id);
        await AssertManifestAsync(id, move: true);
        AssertOriginalsAndDisposedStreams();
    }

    private PathImportPreparation Preparation() => new(options, new PathValidator(options), files.FileSystem,
        new FixedClock(), NullLogger<PathImportPreparation>.Instance);

    private ImportFromPathCommandHandler Handler(IUnitOfWork work, IUploadJobAcceptance acceptance) => new(
        Substitute.For<ITrackedTitleReader>(), Substitute.For<IReferenceCatalogService>(), Substitute.For<ICurrentUser>(),
        Preparation(), new JobWorkspaceExclusion(options), acceptance, work, new FixedClock(),
        NullLogger<ImportFromPathCommandHandler>.Instance);

    private void AssertLeaseHeld(Guid id)
    {
        using var competing = JobWorkspace.TryAcquireExecutionLock(DataRoot, id);
        competing.ShouldBeNull();
    }

    private void AssertOnlyGoodWorkspace(Guid id)
    {
        Directory.GetFiles(Workspace(id), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Workspace(id), path)).ShouldBe(["good.rom"]);
        File.ReadAllBytes(Path.Combine(Workspace(id), "good.rom")).ShouldBe(Good);
    }

    private async Task AssertManifestAsync(Guid id, bool move)
    {
        string path = ImportManifest.PathFor(DataRoot, id);
        File.Exists(path).ShouldBe(move);
        if (!move)
            return;
        var manifest = (await ImportManifest.ReadAsync(path, default)).ShouldNotBeNull();
        var entry = manifest.Entries.ShouldHaveSingleItem();
        entry.WorkspaceRelativePath.ShouldBe("good.rom");
        entry.SourceRelativePath.ShouldBe("originals/good.rom");
        entry.SizeBytes.ShouldBe(Good.Length);
        entry.LastWriteTimeUtc.ShouldBe(File.GetLastWriteTimeUtc(Path.Combine(SourceRoot, "good.rom")));
    }

    private void AssertOriginalsAndDisposedStreams()
    {
        File.ReadAllBytes(Path.Combine(SourceRoot, "good.rom")).ShouldBe(Good);
        File.ReadAllBytes(Path.Combine(SourceRoot, "bad.rom")).ShouldBe(Bad);
        files.Streams.ShouldNotBeEmpty();
        foreach (var stream in files.Streams)
        {
            stream.DisposeCount.ShouldBe(1);
            stream.CanRead.ShouldBeFalse();
            stream.CanWrite.ShouldBeFalse();
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
