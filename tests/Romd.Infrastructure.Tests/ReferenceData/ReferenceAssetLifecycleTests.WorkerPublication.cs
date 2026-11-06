using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed partial class ReferenceAssetLifecycleTests
{
    [Fact]
    public async Task ReferenceBridge_RealBatch_StagesOrderedDuplicateIdentitiesUnderSortedLocksWithoutSaving()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var hashes = new List<Sha256>();
        var locks = new ObserveWorkerFileLocks(new FileMutationLock(db), hashes);
        var files = new FileStorageService(cas, new FileRepository(db), new EfUnitOfWork(db), locks,
            new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, TimeProvider.System);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        await using var publication = await new ReferenceBlobStore(cas, files).PublishStagedAsync([[1, 2], [3], [1, 2]], default);

        publication.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
        (await db.Files.CountAsync()).ShouldBe(0);
        hashes.ShouldBe(hashes.Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal));
        hashes.Count.ShouldBe(2);
        await work.FlushAsync();
        var ids = publication.ReadAfterFlush();
        ids.Count.ShouldBe(3);
        ids.ShouldAllBe(id => id > 0);
        ids[0].ShouldBe(ids[2]);
        ids[1].ShouldNotBe(ids[0]);
        (await db.Files.SingleAsync(row => row.Id == ids[0])).Size.ShouldBe(2);
        (await db.Files.SingleAsync(row => row.Id == ids[1])).Size.ShouldBe(1);
        await transaction.CommitAsync();
        Should.Throw<InvalidOperationException>(() => publication.RequiresFlush);
    }

    [Fact]
    public async Task DeterministicReferenceAdapter_StagesGuardedOrderedRowsWithoutSaving()
    {
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var publication = await TestReferenceBlobs.For(db).PublishStagedAsync([[1], [2], [1]], default);
        publication.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
        (await db.Files.CountAsync()).ShouldBe(0);
        await work.FlushAsync();
        var ids = publication.ReadAfterFlush();
        ids[0].ShouldBe(ids[2]);
        ids[0].ShouldNotBe(ids[1]);
        ids.ShouldAllBe(id => id > 0);
        await publication.DisposeAsync();
        Should.Throw<ObjectDisposedException>(() => publication.ReadAfterFlush());
        await transaction.CommitAsync();
        (await db.Files.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Bootstrap_FreshAndReplay_PreservesDependencySaveAndSkipsDedupIdentityFlush()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)));
        observer.AfterDisposal = async _ =>
        {
            observer.LastIds.ShouldAllBe(id => id > 0);
            (await db.Files.CountAsync()).ShouldBe(observer.LastIds.Distinct().Count());
            (await db.ReferenceAssets.CountAsync()).ShouldBe(0);
        };

        await SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System);

        observer.RequiredFlush.ShouldBeTrue();
        observer.DisposeCount.ShouldBe(1);
        string snapshot = (await db.ReferenceCatalogState.SingleAsync()).Revision;
        int files = await db.Files.CountAsync();
        var preludePending = new RegionEntity { Name = "Before publication prelude", CanonicalKey = "dat-prelude-pending" };
        db.Regions.Add(preludePending);
        var pending = new RegionEntity { Name = "Unrelated pending", CanonicalKey = "dat-pending" };
        observer.BeforePublication = async token =>
        {
            // Alias pruning owns its existing save before publication begins.
            db.Entry(preludePending).State.ShouldBe(EntityState.Unchanged);
            (await db.Regions.CountAsync(row => row.CanonicalKey == "dat-prelude-pending", token)).ShouldBe(1);
            db.Regions.Add(pending);
        };
        observer.AfterDisposal = async _ =>
        {
            observer.RequiredFlush.ShouldBeFalse();
            db.Entry(pending).State.ShouldBe(EntityState.Added);
            (await db.Regions.CountAsync(row => row.CanonicalKey == "dat-pending")).ShouldBe(0);
        };

        await SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System);

        observer.RequiredFlush.ShouldBeFalse();
        observer.DisposeCount.ShouldBe(2);
        (await db.Files.CountAsync()).ShouldBe(files);
        (await db.ReferenceCatalogState.SingleAsync()).Revision.ShouldBe(snapshot);
        (await db.Regions.CountAsync(row => row.CanonicalKey == "dat-pending")).ShouldBe(1);
        (await db.ReferenceAssets.CountAsync(row => row.ContentType == "image/svg+xml")).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Bootstrap_PostIdentityFlushFailure_PreservesPrimaryAndRollsBackWithOptionalSecondaryCleanup(bool cancel, bool secondaryCleanup)
    {
        using var cancellation = new CancellationTokenSource();
        var interruption = new InterruptReferenceSave(false, cancel ? cancellation : null);
        await using var db = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(interruption)));
        var cas = Store();
        var cleanup = new IOException("Secondary reference staging cleanup.");
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)))
        { CleanupFailure = secondaryCleanup ? cleanup : null };

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System, cancellation.Token));

        actual.ShouldBeSameAs(interruption.Primary);
        if (cancel)
            actual.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        if (secondaryCleanup)
            actual!.Data["Romd.ReferencePublication.CleanupFailure"].ShouldBeSameAs(cleanup);
        interruption.Reached.ShouldBeTrue();
        observer.DisposeCount.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
        var orphan = interruption.PublishedHash;
        (await cas.ExistsAsync(StorageKey.FromHash(orphan))).ShouldBeTrue();
        (await Ledger(verify, cas).DeleteOrphanAsync(orphan, default)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_ExistingAssetDependencyFlushFailure_RollsBackFilesAndReservationsAfterSingleDisposal(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var interruption = new InterruptReferenceSave(true, cancel ? cancellation : null);
        await using var db = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(interruption)));
        var cas = Store();
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)));

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System, cancellation.Token));

        actual.ShouldBeSameAs(interruption.Primary);
        if (cancel)
            actual.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        interruption.Reached.ShouldBeTrue();
        observer.DisposeCount.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
    }

    [Fact]
    public async Task Bootstrap_FirstStagingDisposalFailure_AbortsBeforeAssetReservationWithoutRepeatingCleanup()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var primary = new IOException("Pre-reservation cleanup failed.");
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas))) { CleanupFailure = primary };

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System));

        actual.ShouldBeSameAs(primary);
        observer.LastIds.ShouldAllBe(id => id > 0);
        observer.DisposeCount.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
    }

    [Fact]
    public async Task Bootstrap_ExplicitRollbackAlsoFails_RetainsPrimaryAndClearsStagingBeforeOwnedTransactionDisposal()
    {
        var interruption = new InterruptReferenceSave(false, null);
        var rollback = new FailReferenceRollback();
        await using var db = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(interruption, rollback)));
        var cas = Store();

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db,
            new ReferenceBlobStore(cas, Ledger(db, cas)), TimeProvider.System));

        actual.ShouldBeSameAs(interruption.Primary);
        actual!.Data["Romd.ReferenceCatalog.RollbackFailure"].ShouldBeSameAs(rollback.Failure);
        rollback.Called.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
    }

    [Fact]
    public async Task Bootstrap_FinalCatalogCommitFails_RollsBackTypedFamiliesAndPublishedSnapshot()
    {
        var fault = new FailReferenceCommit();
        await using var db = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(fault)));
        var cas = Store();

        (await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db,
            new ReferenceBlobStore(cas, Ledger(db, cas)), TimeProvider.System))).ShouldBeSameAs(fault.Failure);

        fault.SawTypedCatalog.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
    }

    [Fact]
    public async Task Bootstrap_SharedFileCollectorWaitsForPublication_AndRechecksCommittedAssetReference()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        var icons = await CatalogArtworkImport.ReadPlanAsync(RomdCatalogInput.Parse(BundledReferenceData.Document), ct);
        var cas = Store();
        int sharedId;
        await using (var seed = _database.CreateContext())
        {
            var files = Ledger(seed, cas);
            using var bytes = new MemoryStream(icons[0].Content.ToArray(), writable: false);
            await using var staged = await files.StageAsync(bytes, ct);
            var work = new EfUnitOfWork(seed);
            await using var transaction = await work.BeginTransactionAsync(ct);
            var publication = await files.PublishStagedAsync(staged, ct);
            await work.FlushAsync(ct);
            sharedId = publication.ReadAfterFlush().File.Id;
            await transaction.CommitAsync(ct);
        }
        await using var db = _database.CreateContext();
        await using var collector = _database.CreateContext();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)))
        { AfterDisposal = async token => { reached.TrySetResult(); await release.Task.WaitAsync(token); } };
        Task? import = null;
        Task? cleanup = null;
        Exception? primary = null;
        try
        {
            var pendingImport = SharedReferenceDataSeeder.InitializeAsync(db, observer, TimeProvider.System, ct);
            import = pendingImport;
            await reached.Task.WaitAsync(ct);
            observer.LastIds[0].ShouldBe(sharedId);
            int process = await collector.OpenBackendProcessIdAsync(ct);
            var pendingCleanup = Ledger(collector, cas).DeleteIfUnreferencedAsync(sharedId, ct);
            cleanup = pendingCleanup;
            (await _database.WaitsForALockAsync(process, pendingCleanup, ct)).ShouldBeTrue();
            pendingCleanup.IsCompleted.ShouldBeFalse();
            release.TrySetResult();
            await pendingImport;
            (await pendingCleanup).ShouldBeFalse();
            (await collector.ReferenceAssets.CountAsync(row => row.FileId == sharedId, ct)).ShouldBe(1);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, primary, import ?? Task.CompletedTask, cleanup ?? Task.CompletedTask);
        }
    }

    private static async Task AssertEmptyBootstrapAsync(RomdDbContext db)
    {
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.ReferenceAssets.CountAsync()).ShouldBe(0);
        (await db.ReferenceCatalogState.CountAsync()).ShouldBe(0);
        (await db.Platforms.CountAsync()).ShouldBe(0);
        (await db.Companies.CountAsync()).ShouldBe(0);
        (await db.Ratings.CountAsync()).ShouldBe(0);
        (await db.Regions.CountAsync()).ShouldBe(0);
        (await db.GameLanguages.CountAsync()).ShouldBe(0);
    }

    private sealed class ObserveWorkerFileLocks(IFileMutationLock inner, List<Sha256> hashes) : IFileMutationLock
    {
        public IFilePublicationBoundary CapturePublicationBoundary() => inner.CapturePublicationBoundary();
        public Task AcquireAsync(Sha256 hash, CancellationToken ct = default) { hashes.Add(hash); return inner.AcquireAsync(hash, ct); }
    }

    private sealed class ObserveReferenceBridge(IReferenceBlobStore inner) : IReferenceBlobStore
    {
        public Func<CancellationToken, Task>? BeforePublication { get; set; }
        public Func<CancellationToken, Task>? AfterDisposal { get; set; }
        public Exception? CleanupFailure { get; init; }
        public bool RequiredFlush { get; private set; }
        public int DisposeCount { get; private set; }
        public IReadOnlyList<int> LastIds { get; private set; } = [];
        public Task<byte[]?> ReadAsync(string hash, CancellationToken ct) => inner.ReadAsync(hash, ct);
        public async Task<IStagedReferencePublication> PublishStagedAsync(IReadOnlyList<byte[]> contents, CancellationToken ct)
        {
            if (BeforePublication is { } beforePublication)
                await beforePublication(ct);
            var publication = await inner.PublishStagedAsync(contents, ct);
            RequiredFlush = publication.RequiresFlush;
            return new Publication(this, publication, ct);
        }
        private sealed class Publication(ObserveReferenceBridge owner, IStagedReferencePublication inner, CancellationToken ct) : IStagedReferencePublication
        {
            public bool RequiresFlush => inner.RequiresFlush;
            public IReadOnlyList<int> ReadAfterFlush() => owner.LastIds = inner.ReadAfterFlush();
            public async ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                await inner.DisposeAsync();
                if (owner.AfterDisposal is not null)
                    await owner.AfterDisposal(ct);
                if (owner.CleanupFailure is { } failure)
                    throw failure;
            }
        }
    }

    private sealed class InterruptReferenceSave(bool assets, CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public bool Reached { get; private set; }
        public Exception? Primary { get; private set; }
        public Sha256 PublishedHash { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var db = (RomdDbContext)eventData.Context!;
            if (Reached || !await db.Files.AnyAsync(cancellationToken) || await db.ReferenceAssets.AnyAsync(cancellationToken) != assets)
                return result;
            Reached = true;
            PublishedHash = (await db.Files.AsNoTracking().FirstAsync(cancellationToken)).Sha256;
            Exception primary;
            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                primary = new OperationCanceledException("Interrupted after actual bootstrap flush.", cancellationToken);
            }
            else
                primary = new IOException("Interrupted after actual bootstrap flush.");
            Primary = primary;
            throw primary;
        }
    }

    private sealed class FailReferenceRollback : DbTransactionInterceptor
    {
        public IOException Failure { get; } = new("Explicit bootstrap rollback failed.");
        public bool Called { get; private set; }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Called = true; throw Failure; }
    }

    private sealed class FailReferenceCommit : DbTransactionInterceptor
    {
        public IOException Failure { get; } = new("Final bootstrap commit failed.");
        public bool SawTypedCatalog { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var db = (RomdDbContext)eventData.Context!;
            SawTypedCatalog = await db.ReferenceCatalogState.AnyAsync(cancellationToken) && await db.Platforms.AnyAsync(cancellationToken)
                && await db.Ratings.AnyAsync(cancellationToken);
            throw Failure;
        }
    }
}
