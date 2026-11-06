using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.ReferenceData.Assets;
using Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed partial class ReferenceAssetLifecycleTests
{
    private static readonly DateTimeOffset AssetNow = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    [Fact]
    public async Task ReserveStaged_RequiresCurrentLockedBoundaryAndValidIdentity_BeforeTracking()
    {
        await using var db = _database.CreateContext();
        var reservations = new ReferenceAssetReservationStore(db);
        var hash = Sha256.Parse(ReferenceCatalogService.Hash(Png));
        await Should.ThrowAsync<InvalidOperationException>(() => reservations.AcquireAsync(default));
        await Should.ThrowAsync<InvalidOperationException>(() => reservations.ReserveStagedAsync(hash, 1, "image/png", AssetNow, default));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        (await Should.ThrowAsync<OperationCanceledException>(() => reservations.AcquireAsync(cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        var work = new EfUnitOfWork(db);
        await using (var tx = await work.BeginTransactionAsync())
        {
            await Should.ThrowAsync<InvalidOperationException>(() => reservations.ReserveStagedAsync(hash, 1, "image/png", AssetNow, default));
            await reservations.AcquireAsync(default);
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => reservations.ReserveStagedAsync(hash, 0, "image/png", AssetNow, default));
            await Should.ThrowAsync<ArgumentException>(() => reservations.ReserveStagedAsync(default, 1, "image/png", AssetNow, default));
            (await Should.ThrowAsync<OperationCanceledException>(() => reservations.ReserveStagedAsync(hash, 1, "image/png", AssetNow, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await tx.CommitAsync();
        }
        await using var next = await work.BeginTransactionAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => reservations.ReserveStagedAsync(hash, 1, "image/png", AssetNow, default));
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_DedupOnly_RenewsExistingFactsWithoutIdentityFlushOrPrematureUnrelatedSave()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var clock = new ManualTimeProvider(AssetNow);
        string hash = (await AssetHandler(db, Ledger(db, cas), clock: clock).HandleAsync(new(Png, "image/png"))).Value;
        int originalFileId = (await db.ReferenceAssets.SingleAsync()).FileId;
        // Existing content facts are retained even when upload metadata differs from reviewed built-in metadata.
        await db.ReferenceAssets.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ContentType, "image/svg+xml")
            .SetProperty(row => row.ReservedUntil, AssetNow.AddDays(-1)));
        db.ChangeTracker.Clear();
        var unrelated = PendingRegion();
        db.Regions.Add(unrelated);
        var work = new ObserveAssetWork(new EfUnitOfWork(db), db);
        var reservation = new ObserveReservation(new ReferenceAssetReservationStore(db), async () =>
        {
            (await db.Regions.CountAsync()).ShouldBe(0);
            db.Entry(unrelated).State.ShouldBe(EntityState.Added);
            (await db.ReferenceAssets.AsNoTracking().SingleAsync()).ReservedUntil.ShouldBe(Persisted(AssetNow.AddDays(-1)));
        });

        (await AssetHandler(db, Ledger(db, cas), work, clock, reservation).HandleAsync(new(Png, "image/png"))).Value.ShouldBe(hash);

        work.IdentityFlushes.ShouldBe(0);
        var saved = await db.ReferenceAssets.AsNoTracking().SingleAsync();
        saved.FileId.ShouldBe(originalFileId);
        saved.ContentType.ShouldBe("image/svg+xml");
        saved.ReservedUntil.ShouldBe(Persisted(AssetNow.AddDays(7)));
        (await db.Regions.CountAsync()).ShouldBe(1);
        (await db.Files.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(AssetFailure.IdentityFlush, false)]
    [InlineData(AssetFailure.IdentityFlush, true)]
    [InlineData(AssetFailure.ReservationFlush, false)]
    [InlineData(AssetFailure.ReservationFlush, true)]
    public async Task Upload_AfterActualFlush_FaultOrCancellationRollsBackFileReservationAndUnrelatedStaging(AssetFailure failure, bool cancel)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var cas = Store();
        var work = new ObserveAssetWork(new EfUnitOfWork(db), db, failure, cancel ? cancellation : null);
        db.Regions.Add(PendingRegion());
        var handler = AssetHandler(db, Ledger(db, cas), work);
        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(new(Png, "image/png"), cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => handler.HandleAsync(new(Png, "image/png"), cancellation.Token));

        work.SawFileIdentity.ShouldBeTrue();
        work.SawReservation.ShouldBe(failure == AssetFailure.ReservationFlush);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(0);
        (await verification.ReferenceAssets.CountAsync()).ShouldBe(0);
        (await verification.Regions.CountAsync()).ShouldBe(0);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        var hash = Sha256.Parse(ReferenceCatalogService.Hash(Png));
        (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBeTrue();
        (await Ledger(verification, cas).DeleteOrphanAsync(hash, default)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_ActualLateCommitFailure_RollsBackFlushedReservation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new FailAssetCommit(cancel ? cancellation : null);
        await using var db = new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(fault)));
        var handler = AssetHandler(db, Ledger(db, Store()));

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(new(Png, "image/png"), cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => handler.HandleAsync(new(Png, "image/png")));

        fault.SawDurableCandidates.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(0);
        (await verification.ReferenceAssets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Upload_FailedRenewal_RollsBackToExactPriorReservation()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        string hash = (await AssetHandler(db, Ledger(db, cas)).HandleAsync(new(Png, "image/png"))).Value;
        var before = await db.ReferenceAssets.AsNoTracking().SingleAsync();
        var work = new ObserveAssetWork(new EfUnitOfWork(db), db, AssetFailure.ReservationFlush);
        var clock = new ManualTimeProvider(AssetNow.AddDays(10));

        await Should.ThrowAsync<IOException>(() => AssetHandler(db, Ledger(db, cas), work, clock).HandleAsync(new(Png, "image/png")));

        work.IdentityFlushes.ShouldBe(0);
        work.SawReservation.ShouldBeTrue();
        await using var verification = _database.CreateContext();
        var after = await verification.ReferenceAssets.SingleAsync();
        after.Hash.ShouldBe(hash);
        after.FileId.ShouldBe(before.FileId);
        after.ContentType.ShouldBe(before.ContentType);
        after.ReservedUntil.ShouldBe(before.ReservedUntil);
    }

    [Fact]
    public async Task Upload_AfterFlushedRollback_SameContextRetryUsesItsOwnActualFileIdentity()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var failedWork = new ObserveAssetWork(new EfUnitOfWork(db), db, AssetFailure.IdentityFlush);
        await Should.ThrowAsync<IOException>(() => AssetHandler(db, Ledger(db, cas), failedWork).HandleAsync(new(Png, "image/png")));
        db.ChangeTracker.Entries().ShouldBeEmpty();

        var result = await AssetHandler(db, Ledger(db, cas)).HandleAsync(new(Png, "image/png"));

        result.IsError.ShouldBeFalse();
        await using var verification = _database.CreateContext();
        var file = await verification.Files.SingleAsync();
        file.Id.ShouldNotBe(failedWork.FlushedFileId);
        file.Id.ShouldBeGreaterThan(0);
        (await verification.ReferenceAssets.SingleAsync()).FileId.ShouldBe(file.Id);
        (await new Romd.Infrastructure.Storage.ReadOnlyReferenceBlobStore(cas).ReadAsync(result.Value, default)).ShouldBe(Png);
    }

    [Fact]
    public async Task Upload_LostCommitAcknowledgement_KeepsDurableReferenceAndReadableBlob()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var work = new ObserveAssetWork(new EfUnitOfWork(db), db, AssetFailure.LostAcknowledgement);

        await Should.ThrowAsync<IOException>(() => AssetHandler(db, Ledger(db, cas), work).HandleAsync(new(Png, "image/png")));

        work.CommitCompleted.ShouldBeTrue();
        await using var verification = _database.CreateContext();
        var reservation = await verification.ReferenceAssets.SingleAsync();
        (await verification.Files.SingleAsync()).Id.ShouldBe(reservation.FileId);
        (await new ReferenceCatalogService(verification, new Romd.Infrastructure.Storage.ReadOnlyReferenceBlobStore(cas))
            .GetAssetAsync(reservation.Hash, default)).ShouldNotBeNull().Bytes.ShouldBe(Png);
    }

    [Fact]
    public async Task Upload_StagingDisposalFailsAfterPublication_RollsBackAcceptanceWithoutDeletingSharedCas()
    {
        await using var db = _database.CreateContext();
        var cas = Store();
        var real = Ledger(db, cas);
        var files = Substitute.For<IFileStorageService>();
        BrokenStaging? staged = null;
        files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            staged = new BrokenStaging(await real.StageAsync(call.Arg<Stream>(), call.Arg<CancellationToken>()));
            return (IStagedFile)staged;
        });
        files.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(call =>
            real.PublishStagedAsync(((BrokenStaging)call.Arg<IStagedFile>()).Inner, call.Arg<CancellationToken>()));

        await Should.ThrowAsync<IOException>(() => AssetHandler(db, files).HandleAsync(new(Png, "image/png")));

        staged.ShouldNotBeNull().DisposeCount.ShouldBe(1);
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(0);
        (await verification.ReferenceAssets.CountAsync()).ShouldBe(0);
        (await cas.ExistsAsync(StorageKey.FromHash(Sha256.Parse(ReferenceCatalogService.Hash(Png))))).ShouldBeTrue();
    }

    private static DateTimeOffset Persisted(DateTimeOffset input) => DateTimeOffset.FromUnixTimeMilliseconds(input.ToUnixTimeMilliseconds());
    private static RegionEntity PendingRegion() => new() { Name = "Pending", CanonicalKey = "dat-pending", CreatedAt = AssetNow };
    private static UploadReferenceAssetCommandHandler AssetHandler(RomdDbContext db, IFileStorageService files,
        IUnitOfWork? work = null, TimeProvider? clock = null, IReferenceAssetReservationStore? reservations = null) =>
        new(reservations ?? new ReferenceAssetReservationStore(db), files, work ?? new EfUnitOfWork(db),
            clock ?? new ManualTimeProvider(AssetNow), NullLogger<UploadReferenceAssetCommandHandler>.Instance);

    public enum AssetFailure { None, IdentityFlush, ReservationFlush, LostAcknowledgement }

    private sealed class ObserveReservation(IReferenceAssetReservationStore inner, Func<Task> afterStaging) : IReferenceAssetReservationStore
    {
        public Task AcquireAsync(CancellationToken ct) => inner.AcquireAsync(ct);
        public async Task ReserveStagedAsync(Sha256 hash, int fileId, string contentType, DateTimeOffset reservedUntil, CancellationToken ct)
        {
            await inner.ReserveStagedAsync(hash, fileId, contentType, reservedUntil, ct);
            await afterStaging();
        }
    }

    private sealed class ObserveAssetWork(IUnitOfWork inner, RomdDbContext db, AssetFailure failure = AssetFailure.None,
        CancellationTokenSource? cancellation = null) : IUnitOfWork
    {
        public bool SawFileIdentity { get; private set; }
        public bool SawReservation { get; private set; }
        public bool CommitCompleted { get; private set; }
        public int IdentityFlushes { get; private set; }
        public int FlushedFileId { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            IdentityFlushes++;
            await inner.FlushAsync(cancellationToken);
            FlushedFileId = (await db.Files.SingleAsync(cancellationToken)).Id;
            SawFileIdentity = FlushedFileId > 0 && !await db.ReferenceAssets.AnyAsync(cancellationToken);
            SawFileIdentity.ShouldBeTrue();
            if (failure == AssetFailure.IdentityFlush)
                await InterruptAsync(cancellationToken);
        }
        private async Task InterruptAsync(CancellationToken ct)
        {
            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected reference acceptance failure.");
        }
        private async Task CommitAsync(ITransaction transaction, CancellationToken ct)
        {
            if (failure == AssetFailure.ReservationFlush)
            {
                await inner.FlushAsync(ct);
                SawReservation = await db.ReferenceAssets.AnyAsync(ct);
                SawReservation.ShouldBeTrue();
                await InterruptAsync(ct);
            }
            await transaction.CommitAsync(ct);
            CommitCompleted = true;
            if (failure == AssetFailure.LostAcknowledgement)
                throw new IOException("Lost acknowledgement after actual commit.");
        }
        private sealed class Transaction(ObserveAssetWork owner, ITransaction transaction) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.CommitAsync(transaction, cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }

    private sealed class FailAssetCommit(CancellationTokenSource? cancellation) : DbTransactionInterceptor
    {
        public bool SawDurableCandidates { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var db = (RomdDbContext)eventData.Context!;
            SawDurableCandidates = await db.ReferenceAssets.AnyAsync(cancellationToken) && await db.Files.AnyAsync(cancellationToken);
            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected actual database commit failure.");
        }
    }

    private sealed class BrokenStaging(IStagedFile inner) : IStagedFile
    {
        public IStagedFile Inner => inner;
        public Sha256 Sha256 => inner.Sha256;
        public long Size => inner.Size;
        public Md5? ArtifactMd5 => inner.ArtifactMd5;
        public Sha1? ArtifactSha1 => inner.ArtifactSha1;
        public int DisposeCount { get; private set; }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await inner.DisposeAsync();
            throw new IOException("Injected staging cleanup failure before reservation acceptance.");
        }
    }
}
