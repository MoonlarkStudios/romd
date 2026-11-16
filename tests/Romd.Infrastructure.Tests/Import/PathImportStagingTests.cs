using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

/// <summary>
///     The worker's Copying phase: it re-inspects the accepted source under its own allowlist and
///     copies it into the job workspace. A failed or cancelled attempt leaves an empty workspace
///     and no move manifest, so redelivery starts over and extraction never sees a partial copy.
/// </summary>
public sealed class PathImportStagingTests : IDisposable
{
    private static readonly byte[] Good = [1, 2, 3, 4];
    private static readonly byte[] Bad = [5, 6, 7, 8];
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private readonly string root = Directory.CreateTempSubdirectory("romd-path-staging-").FullName;
    private readonly IRomdOptions options = Substitute.For<IRomdOptions>();
    private readonly PathCopyFailureFileSystem files = new();
    private string DataRoot => Path.Combine(root, "data");
    private string AllowedRoot => Path.Combine(root, "allowed");
    private string SourceRoot => Path.Combine(AllowedRoot, "originals");
    private string Workspace(Guid id) => Path.Combine(DataRoot, "temp", "jobs", id.ToString("N"));

    public PathImportStagingTests()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllBytes(Path.Combine(SourceRoot, "good.rom"), Good);
        File.WriteAllBytes(Path.Combine(SourceRoot, "bad.rom"), Bad);
        options.DataDirectory.Returns(DataRoot);
        options.AllowedImportPaths.Returns([AllowedRoot]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_CopiesTreePreservingRelativePathsAndReportsEachFile(bool move)
    {
        Directory.CreateDirectory(Path.Combine(SourceRoot, "nested", "deep"));
        File.WriteAllBytes(Path.Combine(SourceRoot, "nested", "deep", "leaf.rom"), [9]);
        var (job, lease) = await AcceptedAsync(move);
        await using var _ = lease;
        var reported = new List<string>();

        var result = await Staging().StageAsync(job, Workspace(job.Id), (path, _) =>
        { reported.Add(path); return Task.CompletedTask; }, default);

        result.Value.Issues.ShouldBeEmpty();
        reported.ShouldBe(["bad.rom", "good.rom", Path.Combine("nested", "deep", "leaf.rom")], ignoreOrder: true);
        File.ReadAllBytes(Path.Combine(Workspace(job.Id), "good.rom")).ShouldBe(Good);
        File.ReadAllBytes(Path.Combine(Workspace(job.Id), "nested", "deep", "leaf.rom")).ShouldBe(new byte[] { 9 });
        File.Exists(ImportManifest.PathFor(DataRoot, job.Id)).ShouldBe(move);
        AssertOriginals();
    }

    [Fact]
    public async Task Stage_MoveManifestRecordsRootRelativeFingerprintsOutsideTheWorkspace()
    {
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        (await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default)).IsError.ShouldBeFalse();

        string manifestPath = ImportManifest.PathFor(DataRoot, job.Id);
        Path.GetDirectoryName(manifestPath).ShouldNotBe(Workspace(job.Id));
        var manifest = (await ImportManifest.ReadAsync(manifestPath, default)).ShouldNotBeNull();
        manifest.Version.ShouldBe(ImportManifest.CurrentVersion);
        manifest.AllowedRoot.ShouldBe(AllowedRoot);
        manifest.SourceRoot.ShouldBe("originals");
        manifest.Move.ShouldBeTrue();
        foreach (var entry in manifest.Entries)
        {
            entry.SourceRelativePath.ShouldBe("originals/" + entry.WorkspaceRelativePath);
            var original = new FileInfo(Path.Combine(AllowedRoot, entry.SourceRelativePath));
            entry.SizeBytes.ShouldBe(original.Length);
            entry.LastWriteTimeUtc.ShouldBe(original.LastWriteTimeUtc);
        }
        // THE move-mode safety invariant: the workspace holds only copies, so deleting it never
        // destroys an original and never removes the manifest that fences later source deletion.
        Directory.Delete(Workspace(job.Id), recursive: true);
        AssertOriginals();
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_MixedSuccessAndPartialCopyFailure_AdmitsOnlyCompleteFileAndMoveEntry(bool move)
    {
        files.CopyFailures["bad.rom"] = new IOException("source failed after its prefix");
        var (job, lease) = await AcceptedAsync(move);
        await using var _ = lease;
        files.BeforeDelete = _ => AssertLeaseHeld(job.Id);

        var result = await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default);

        var issue = result.Value.Issues.ShouldHaveSingleItem();
        issue.Item.ShouldBe(Path.Combine(SourceRoot, "bad.rom"));
        issue.Message.ShouldContain("source failed after its prefix");
        issue.OccurredAt.ShouldBe(Now);
        files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(new byte[] { 5, 6 });
        AssertOnlyGoodWorkspace(job.Id);
        await AssertManifestAsync(job.Id, move);
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Stage_TargetDisposalFails_ExcludesFailedFileFromWorkspaceAndMoveManifest()
    {
        files.TargetDisposalFailures["bad.rom"] = new IOException("target flush failed");
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        var result = await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default);

        result.Value.Issues.ShouldHaveSingleItem().Message.ShouldContain("target flush failed");
        files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(Bad);
        AssertOnlyGoodWorkspace(job.Id);
        await AssertManifestAsync(job.Id, move: true);
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Stage_AllCopiesFail_EmptiesWorkspaceAndReturnsNoFilesWithoutTouchingOriginals()
    {
        files.CopyFailures["bad.rom"] = new IOException("bad failed");
        files.CopyFailures["good.rom"] = new UnauthorizedAccessException("good failed after writing");
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        var result = await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default);

        result.FirstError.Code.ShouldBe("Import.NoFilesFound");
        files.DeleteAttempts.Count.ShouldBe(2);
        AssertEmptyWorkspace(job.Id);
        AssertOriginalsAndDisposedStreams();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_PartialTargetCannotBeExcluded_ThrowsCopyCauseWithCleanupCauseAndEmptiesWorkspace(bool silentFailure)
    {
        var primary = new IOException("source failed after its prefix");
        var secondary = new UnauthorizedAccessException("target cannot be deleted");
        files.CopyFailures["bad.rom"] = primary;
        files.DeleteFailure = silentFailure ? null : secondary;
        files.IgnoreDelete = silentFailure;
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        var observed = await CaptureAsync(() => Staging().StageAsync(job, Workspace(job.Id), NoProgress, default));

        observed.ShouldBeSameAs(primary);
        var cleanup = primary.Data[PathImportFiles.TargetCleanupFailureKey].ShouldBeAssignableTo<Exception>().ShouldNotBeNull();
        if (silentFailure)
            cleanup.ShouldBeOfType<IOException>().Message.ShouldContain("remains");
        else
            cleanup.ShouldBeSameAs(secondary);
        AssertEmptyWorkspace(job.Id);
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Stage_CancellationAfterWritingPrefix_PreservesExactCancellationAndEmptiesWorkspace()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("copy cancelled after its prefix", cancellation.Token);
        files.CopyFailures["bad.rom"] = primary;
        files.BeforeCopyFailure = cancellation.Cancel;
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        var observed = await CaptureAsync(() => Staging().StageAsync(job, Workspace(job.Id), NoProgress, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        AssertEmptyWorkspace(job.Id);
        AssertOriginalsAndDisposedStreams();
    }

    [Fact]
    public async Task Stage_Redelivery_DiscardsBytesAndManifestFromAnEarlierAttempt()
    {
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;
        Directory.CreateDirectory(Path.Combine(Workspace(job.Id), "stale"));
        File.WriteAllBytes(Path.Combine(Workspace(job.Id), "stale", "partial.rom"), [0]);
        File.WriteAllBytes(Path.Combine(Workspace(job.Id), "good.rom"), [0, 0]);
        File.WriteAllText(ImportManifest.PathFor(DataRoot, job.Id), "{ stale }");
        File.Delete(Path.Combine(SourceRoot, "bad.rom"));

        (await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default)).IsError.ShouldBeFalse();

        AssertOnlyGoodWorkspace(job.Id);
        await AssertManifestAsync(job.Id, move: true);
    }

    [Fact]
    public async Task Stage_FileRejectedAfterEnumerationIsNeverOpenedOrCopied()
    {
        var validator = Substitute.For<IPathValidator>();
        validator.ValidateImportRoot(Arg.Any<string>()).Returns(AllowedRoot);
        validator.ValidateImportFile(Arg.Any<string>())
            .Returns(Error.Validation("Import.UnsafePath", "Source changed after inspection."));
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;

        var result = await Staging(validator).StageAsync(job, Workspace(job.Id), NoProgress, default);

        result.FirstError.ShouldBe(ImportErrors.NoFilesFound(SourceRoot));
        files.Streams.ShouldBeEmpty();
        AssertEmptyWorkspace(job.Id);
        AssertOriginals();
    }

    [Fact]
    public async Task Stage_SymlinkInSource_IsRecordedAndNeverFollowed()
    {
        string outside = Path.Combine(root, "outside.rom");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(Path.Combine(SourceRoot, "link.rom"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Symlink creation is not permitted on this platform.
        }
        var (job, lease) = await AcceptedAsync(move: false);
        await using var _ = lease;

        var result = await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default);

        result.Value.Issues.ShouldContain(issue => issue.Message.Contains("symbolic link", StringComparison.Ordinal));
        File.Exists(Path.Combine(Workspace(job.Id), "link.rom")).ShouldBeFalse();
        File.Exists(Path.Combine(Workspace(job.Id), "good.rom")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disallowed")]
    public async Task Stage_SourceUnavailableToTheWorker_ReturnsTheValidationErrorWithoutCopying(string change)
    {
        var (job, lease) = await AcceptedAsync(move: true);
        await using var _ = lease;
        if (change == "missing")
            Directory.Delete(SourceRoot, recursive: true);
        else
            options.AllowedImportPaths.Returns([Path.Combine(root, "elsewhere")]);

        var result = await Staging().StageAsync(job, Workspace(job.Id), NoProgress, default);

        result.FirstError.Code.ShouldBe(change == "missing" ? "Import.PathNotFound" : "Import.PathNotAllowed");
        AssertEmptyWorkspace(job.Id);
    }

    [Fact]
    public async Task Stage_RequiresAPathImportAndItsOwnWorkspace()
    {
        var upload = UploadJob.Create("upload.zip", Now);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Staging().StageAsync(upload, Workspace(upload.Id), NoProgress, default));
        var (job, lease) = await AcceptedAsync(move: false);
        await using var _ = lease;
        await Should.ThrowAsync<ArgumentException>(() =>
            Staging().StageAsync(job, Workspace(Guid.NewGuid()), NoProgress, default));
    }

    private PathImportStaging Staging(IPathValidator? validator = null) => new(options,
        validator ?? new PathValidator(options), files.FileSystem, new FixedClock(), NullLogger<PathImportStaging>.Instance);

    /// <summary>Mirrors acceptance plus the runner: a reserved, empty workspace under the job's lock.</summary>
    private async Task<(UploadJob Job, IAsyncDisposable Lease)> AcceptedAsync(bool move)
    {
        var job = UploadJob.Create("originals/", Now);
        job.SetImportSource(SourceRoot, move);
        Directory.CreateDirectory(Workspace(job.Id));
        var lease = await new JobWorkspaceExclusion(options).AcquireAsync(job.Id);
        return (job, lease);
    }

    private static Task NoProgress(string path, CancellationToken ct) => Task.CompletedTask;

    private void AssertLeaseHeld(Guid id)
    {
        using var competing = JobWorkspace.TryAcquireExecutionLock(DataRoot, id);
        competing.ShouldBeNull();
    }

    private void AssertEmptyWorkspace(Guid id)
    {
        Directory.Exists(Workspace(id)).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(Workspace(id)).ShouldBeEmpty();
        File.Exists(ImportManifest.PathFor(DataRoot, id)).ShouldBeFalse();
    }

    private void AssertOnlyGoodWorkspace(Guid id)
    {
        Directory.GetFiles(Workspace(id), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Workspace(id), path)).ShouldBe(["good.rom"]);
        Directory.GetDirectories(Workspace(id)).ShouldBeEmpty();
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

    private void AssertOriginals()
    {
        File.ReadAllBytes(Path.Combine(SourceRoot, "good.rom")).ShouldBe(Good);
        File.ReadAllBytes(Path.Combine(SourceRoot, "bad.rom")).ShouldBe(Bad);
    }

    private void AssertOriginalsAndDisposedStreams()
    {
        AssertOriginals();
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
