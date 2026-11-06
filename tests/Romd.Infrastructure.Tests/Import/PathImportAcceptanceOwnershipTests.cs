using System.IO.Abstractions;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathImportAcceptanceOwnershipTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T02:00:00.123456Z");
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string root = Directory.CreateTempSubdirectory("romd-path-import-owner-").FullName;
    private readonly Guid actor = Guid.NewGuid();
    private string DataRoot => Path.Combine(root, "data");
    private string AllowedRoot => Path.Combine(root, "allowed");
    private string SourceRoot => Path.Combine(AllowedRoot, "originals");
    private string Workspace(Guid id) => Path.Combine(DataRoot, "temp", "jobs", $"{id:N}");

    public PathImportAcceptanceOwnershipTests()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.Combine(SourceRoot, "nested"));
        File.WriteAllBytes(Path.Combine(SourceRoot, "top.rom"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(SourceRoot, "nested", "leaf.rom"), [4, 5]);
        using var db = database.CreateContext();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Selected", ShortName = "selected" });
        db.Users.Add(new RomdUser { Id = actor, UserName = "path-importer" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CopyOrMove_CommitsExactUploadAndDispatchAfterPreparingBytesOutsideTransaction(bool move)
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        work.AfterSqlFlush = async token =>
        {
            (await db.Jobs.CountAsync(token)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(token)).ShouldBe(1);
            await AssertNoJobsAsync();
        };

        var result = await Handler(db, work).HandleAsync(new(SourceRoot, "selected", 7, true, true, true, move));

        result.IsError.ShouldBeFalse();
        work.PreparedBeforeBegin.ShouldBeTrue();
        work.Committed.ShouldBeTrue();
        work.Disposed.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value, move, maxParallel: 7, allOptions: true);
        await AssertRetainedAsync(result.Value, move);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_FaultOrCancellationAfterActualCommitFlush_RollsBackDatabaseAndRetainsWorkspaceAndMoveManifest(bool cancel, bool cleanupFault)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = Work(db);
        var cause = new IOException("path acceptance fault after SQL flush");
        work.CleanupFault = cleanupFault;
        work.AfterSqlFlush = async token =>
        {
            (await db.Jobs.CountAsync(token)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(token)).ShouldBe(1);
            await AssertNoJobsAsync();
            if (cancel)
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
            throw cause;
        };

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(new(SourceRoot, Move: true), cancellation.Token));

        if (cancel)
        {
            var cancelled = error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull();
            cancelled.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            error.ShouldBeSameAs(cause);
        work.Flushed.ShouldBeTrue();
        work.CommitAttempted.ShouldBeTrue();
        work.Committed.ShouldBeFalse();
        work.Disposed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertNoJobsAsync();
        await AssertRetainedAsync(PreparedId(work), move: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_UncertainAcknowledgement_RetainsCompletedWorkspaceAndManifestRegardlessOfDatabaseRead(bool databaseCommitted)
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        var cause = new IOException("uncertain path import acknowledgement");
        work.CleanupFault = true;
        if (databaseCommitted)
            work.AfterCommit = _ => Task.FromException(cause);
        else
            work.AfterSqlFlush = _ => Task.FromException(cause);

        var error = await CaptureAsync(() => Handler(db, work).HandleAsync(new(SourceRoot, "selected", Move: true)));

        error.ShouldBeSameAs(cause);
        work.CommitAttempted.ShouldBeTrue();
        work.Committed.ShouldBe(databaseCommitted);
        work.Disposed.ShouldBeTrue();
        var id = PreparedId(work);
        if (databaseCommitted)
            await AssertAcceptedAsync(id, move: true);
        else
            await AssertNoJobsAsync();
        await AssertRetainedAsync(id, move: true);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_DisposalFailsAfterAcknowledgedCommit_ReturnsDurableAcceptanceAndRetainsMoveInputs()
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        work.CleanupFault = true;

        var result = await Handler(db, work).HandleAsync(new(SourceRoot, "selected", Move: true));

        result.IsError.ShouldBeFalse();
        work.Committed.ShouldBeTrue();
        work.Disposed.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value, move: true);
        await AssertRetainedAsync(result.Value, move: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_FailureBeforeCommitAttempt_DiscardsCompletedWorkspaceAndManifestAfterTransactionDisposalUnderLease(bool failBegin)
    {
        await using var db = database.CreateContext();
        var work = Work(db);
        var cause = new IOException("path acceptance failed before commit attempt");
        if (failBegin)
            work.BeforeBegin = _ => Task.FromException(cause);
        var acceptance = new FaultAfterStage(new UploadJobRepository(db, new FixedClock()), cause);

        var error = await CaptureAsync(() => Handler(db, work, acceptance).HandleAsync(new(SourceRoot, Move: true)));

        error.ShouldBeSameAs(cause);
        work.CommitAttempted.ShouldBeFalse();
        work.Disposed.ShouldBe(!failBegin);
        var id = PreparedId(work);
        Directory.Exists(Workspace(id)).ShouldBeFalse();
        File.Exists(ImportManifest.PathFor(DataRoot, id)).ShouldBeFalse();
        await AssertOriginalsAsync();
        await AssertNoJobsAsync();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        using var nextOwner = JobWorkspace.TryAcquireExecutionLock(DataRoot, id).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Handle_PathConcurrencyOutsideGenericUploadRange_PreservesExistingAcceptedValue(int maxParallel)
    {
        await using var db = database.CreateContext();

        var result = await Handler(db, Work(db)).HandleAsync(new(SourceRoot, "selected", MaxParallelRoms: maxParallel));

        result.IsError.ShouldBeFalse();
        await AssertAcceptedAsync(result.Value, move: false, maxParallel);
        await AssertRetainedAsync(result.Value, move: false);
    }

    [Fact]
    public async Task Handle_SamePathTwice_AcceptsFreshJobAndCorrelationIdentitiesWithIndependentInputs()
    {
        await using var db = database.CreateContext();
        var handler = Handler(db, Work(db));

        var first = await handler.HandleAsync(new(SourceRoot));
        var second = await handler.HandleAsync(new(SourceRoot));

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();
        first.Value.ShouldNotBe(second.Value);
        await using var read = database.CreateContext();
        var jobs = await read.Jobs.OfType<UploadJobEntity>().ToListAsync();
        jobs.Count.ShouldBe(2);
        jobs.Select(job => job.Id).ShouldBe([first.Value, second.Value], ignoreOrder: true);
        jobs.Select(job => job.CorrelationId).Distinct().Count().ShouldBe(2);
        jobs.ShouldAllBe(job => job.PlatformId == null && job.MaxParallelRoms == 4 && job.ImportSourcePath == SourceRoot);
        (await read.JobDispatches.Select(dispatch => dispatch.JobId).ToListAsync()).ShouldBe([first.Value, second.Value], ignoreOrder: true);
        await AssertRetainedAsync(first.Value, move: false);
        await AssertRetainedAsync(second.Value, move: false);
    }

    private ImportFromPathCommandHandler Handler(RomdDbContext db, ObservedWork work, IUploadJobAcceptance? acceptance = null)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(DataRoot);
        options.AllowedImportPaths.Returns([AllowedRoot]);
        var tracked = Substitute.For<ITrackedTitleReader>();
        tracked.HasTrackedAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns(true);
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.ResolveSystemIdAsync("selected", Arg.Any<CancellationToken>()).Returns(1);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        var preparation = new PathImportPreparation(options, new PathValidator(options), new FileSystem(), new FixedClock(),
            NullLogger<PathImportPreparation>.Instance);
        return new(tracked, reference, user, new ObservedPreparation(preparation, work), new JobWorkspaceExclusion(options),
            acceptance ?? new UploadJobRepository(db, new FixedClock()), work, new FixedClock(), NullLogger<ImportFromPathCommandHandler>.Instance);
    }

    private ObservedWork Work(RomdDbContext db) => new(new EfUnitOfWork(db), DataRoot);
    private static Guid PreparedId(ObservedWork work)
    {
        work.JobId.HasValue.ShouldBeTrue();
        return work.JobId.GetValueOrDefault();
    }

    private async Task AssertAcceptedAsync(Guid id, bool move, int maxParallel = 4, bool allOptions = false)
    {
        await using var read = database.CreateContext();
        var job = (await read.Jobs.OfType<UploadJobEntity>().ToListAsync()).ShouldHaveSingleItem();
        job.Id.ShouldBe(id);
        job.Id.ShouldNotBe(Guid.Empty);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.SourceFilename.ShouldBe("originals/");
        job.ImportSourcePath.ShouldBe(SourceRoot);
        job.ImportMove.ShouldBe(move);
        job.CreatedByUserId.ShouldBe(actor);
        job.PlatformId.ShouldBe(1);
        job.CreatedAt.ShouldBe(PersistedNow);
        job.Phase.ShouldBe("Pending");
        job.MaxParallelRoms.ShouldBe(maxParallel);
        job.AllowUnidentified.ShouldBe(allOptions);
        job.ArchiveOnly.ShouldBe(allOptions);
        job.TrackedOnly.ShouldBe(allOptions);
        job.GetErrors().ShouldBeEmpty();
        var dispatch = (await read.JobDispatches.ToListAsync()).ShouldHaveSingleItem();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe("upload");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
    }

    private async Task AssertNoJobsAsync()
    {
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    private async Task AssertOriginalsAsync()
    {
        (await File.ReadAllBytesAsync(Path.Combine(SourceRoot, "top.rom"))).ShouldBe(new byte[] { 1, 2, 3 });
        (await File.ReadAllBytesAsync(Path.Combine(SourceRoot, "nested", "leaf.rom"))).ShouldBe(new byte[] { 4, 5 });
    }

    private async Task AssertRetainedAsync(Guid id, bool move)
    {
        await AssertOriginalsAsync();
        (await File.ReadAllBytesAsync(Path.Combine(Workspace(id), "top.rom"))).ShouldBe(new byte[] { 1, 2, 3 });
        (await File.ReadAllBytesAsync(Path.Combine(Workspace(id), "nested", "leaf.rom"))).ShouldBe(new byte[] { 4, 5 });
        File.Exists(ImportManifest.PathFor(DataRoot, id)).ShouldBe(move);
        if (move)
        {
            var manifest = (await ImportManifest.ReadAsync(ImportManifest.PathFor(DataRoot, id), default)).ShouldNotBeNull();
            manifest.Version.ShouldBe(ImportManifest.CurrentVersion);
            manifest.AllowedRoot.ShouldBe(AllowedRoot);
            manifest.SourceRoot.ShouldBe("originals");
            manifest.Move.ShouldBeTrue();
            manifest.Entries.Select(entry => entry.WorkspaceRelativePath).ShouldBe(["top.rom", "nested/leaf.rom"], ignoreOrder: true);
            foreach (var entry in manifest.Entries)
            {
                entry.SourceRelativePath.ShouldBe("originals/" + entry.WorkspaceRelativePath);
                var original = new FileInfo(Path.Combine(AllowedRoot, entry.SourceRelativePath));
                entry.SizeBytes.ShouldBe(original.Length);
                entry.LastWriteTimeUtc.ShouldBe(original.LastWriteTimeUtc);
            }
            Directory.GetFiles(Workspace(id), "*.import.json", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        using var successor = JobWorkspace.TryAcquireExecutionLock(DataRoot, id).ShouldNotBeNull();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    public void Dispose()
    {
        database.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FaultAfterStage(IUploadJobAcceptance inner, Exception cause) : IUploadJobAcceptance
    {
        public async Task AddStagedAsync(UploadJob job, CancellationToken ct = default)
        {
            await inner.AddStagedAsync(job, ct);
            throw cause;
        }
    }

    private sealed class ObservedPreparation(IPathImportPreparation inner, ObservedWork work) : IPathImportPreparation
    {
        public ErrorOr<IPathImportPlan> Inspect(string sourcePath, CancellationToken ct = default) => inner.Inspect(sourcePath, ct);
        public async Task<ErrorOr<PreparedPathImport>> PrepareAsync(IPathImportPlan plan, Guid jobId, bool move,
            IJobWorkspaceLease lease, CancellationToken ct = default)
        {
            work.JobId = jobId;
            return await inner.PrepareAsync(plan, jobId, move, lease, ct);
        }
    }

    private sealed class ObservedWork(IUnitOfWork inner, string dataRoot) : IUnitOfWork
    {
        public Guid? JobId { get; set; }
        public bool PreparedBeforeBegin { get; private set; }
        public bool Flushed { get; private set; }
        public bool CommitAttempted { get; private set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public bool CleanupFault { get; set; }
        public Func<CancellationToken, Task>? BeforeBegin { get; set; }
        public Func<CancellationToken, Task>? AfterSqlFlush { get; set; }
        public Func<CancellationToken, Task>? AfterCommit { get; set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Path acceptance has no generated identity and must use its transaction commit flush.");

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            var id = PreparedId(this);
            File.Exists(Path.Combine(dataRoot, "temp", "jobs", $"{id:N}", "top.rom")).ShouldBeTrue();
            PreparedBeforeBegin = true;
            AssertLeaseHeld();
            if (BeforeBegin is not null)
                await BeforeBegin(cancellationToken);
            return new ObservedTransaction(await inner.BeginTransactionAsync(cancellationToken), this);
        }

        private void AssertLeaseHeld()
        {
            using var contender = JobWorkspace.TryAcquireExecutionLock(dataRoot, PreparedId(this));
            contender.ShouldBeNull();
        }

        private void AssertInputRetained() =>
            File.Exists(Path.Combine(dataRoot, "temp", "jobs", $"{PreparedId(this):N}", "top.rom")).ShouldBeTrue();

        private async Task FlushForCommitAsync(CancellationToken ct)
        {
            await inner.FlushAsync(ct);
            Flushed = true;
            if (AfterSqlFlush is not null)
                await AfterSqlFlush(ct);
        }

        private sealed class ObservedTransaction(ITransaction transaction, ObservedWork owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                owner.CommitAttempted = true;
                owner.AssertLeaseHeld();
                await owner.FlushForCommitAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                owner.Committed = true;
                owner.AssertLeaseHeld();
                if (owner.AfterCommit is not null)
                    await owner.AfterCommit(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                owner.AssertLeaseHeld();
                await transaction.DisposeAsync();
                owner.Disposed = true;
                owner.AssertLeaseHeld();
                owner.AssertInputRetained();
                if (owner.CleanupFault)
                    throw new IOException("path acceptance transaction disposal failed");
            }
        }
    }
}
