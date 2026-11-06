using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.ReferenceData.Assets;
using Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed partial class ReferenceAssetLifecycleTests
{
    [Fact]
    public async Task Upload_ConcurrentSameHash_SerializesAtReferenceLockAndKeepsCanonicalFile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var cas = Store();
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        var gate = new AssetGate();
        var firstHandler = AssetHandler(firstDb, Ledger(firstDb, cas), reservations:
            new GateReservation(new ReferenceAssetReservationStore(firstDb), gate));
        Task? first = null;
        Task? second = null;
        Exception? primary = null;
        try
        {
            var firstUpload = firstHandler.HandleAsync(new UploadReferenceAssetCommand(Png, "image/png"), ct);
            first = firstUpload;
            await gate.Entered.Task.WaitAsync(ct);
            int secondProcess = await secondDb.OpenBackendProcessIdAsync(ct);
            var secondUpload = AssetHandler(secondDb, Ledger(secondDb, cas)).HandleAsync(new(Png, "image/png"), ct);
            second = secondUpload;
            (await _database.WaitsForALockAsync(secondProcess, secondUpload, ct)).ShouldBeTrue();
            firstUpload.IsCompleted.ShouldBeFalse();
            secondUpload.IsCompleted.ShouldBeFalse();
            gate.Release.TrySetResult();
            var results = await Task.WhenAll(firstUpload, secondUpload);

            results.ShouldAllBe(result => !result.IsError);
            results[0].Value.ShouldBe(results[1].Value);
            await using var verification = _database.CreateContext();
            var file = await verification.Files.SingleAsync(ct);
            var reservation = await verification.ReferenceAssets.SingleAsync(ct);
            reservation.Hash.ShouldBe(file.Sha256.ToString());
            reservation.FileId.ShouldBe(file.Id);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            gate.Release.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary,
                first ?? Task.CompletedTask, second ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task Upload_RenewalAndExpiry_SerializeAndRecheckCommittedDeadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var cas = Store();
        await using var publisher = _database.CreateContext();
        await AssetHandler(publisher, Ledger(publisher, cas)).HandleAsync(new(Png, "image/png"), ct);
        await publisher.ReferenceAssets.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReservedUntil, AssetNow.AddDays(-1)), ct);
        publisher.ChangeTracker.Clear();
        var gate = new AssetGate();
        await using var expiryDb = _database.CreateContext();
        Task? upload = null;
        Task? expiry = null;
        Exception? primary = null;
        try
        {
            var pendingUpload = AssetHandler(publisher, Ledger(publisher, cas), reservations:
                new GateReservation(new ReferenceAssetReservationStore(publisher), gate)).HandleAsync(new(Png, "image/png"), ct);
            upload = pendingUpload;
            await gate.Entered.Task.WaitAsync(ct);
            int expiryProcess = await expiryDb.OpenBackendProcessIdAsync(ct);
            var pendingExpiry = ReferenceCatalogService.ReleaseExpiredAssetsAsync(expiryDb, AssetNow, ct);
            expiry = pendingExpiry;
            (await _database.WaitsForALockAsync(expiryProcess, pendingExpiry, ct)).ShouldBeTrue();
            pendingExpiry.IsCompleted.ShouldBeFalse();
            gate.Release.TrySetResult();

            (await pendingUpload).IsError.ShouldBeFalse();
            (await pendingExpiry).ShouldBe(0);
            (await expiryDb.ReferenceAssets.SingleAsync(ct)).ReservedUntil.ShouldBe(Persisted(AssetNow.AddDays(7)));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            gate.Release.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary,
                upload ?? Task.CompletedTask, expiry ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task Upload_FileCollectorWaitsForPublisher_ThenRechecksCommittedReservation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var cas = Store();
        await using var publisher = _database.CreateContext();
        // Collection must see an already committed file candidate before it can wait on its lock.
        var files = Ledger(publisher, cas);
        using (var content = new MemoryStream(Png, writable: false))
        await using (var staged = await files.StageAsync(content, ct))
        {
            var work = new EfUnitOfWork(publisher);
            await using var tx = await work.BeginTransactionAsync(ct);
            var publication = await files.PublishStagedAsync(staged, ct);
            await work.FlushAsync(ct);
            publication.ReadAfterFlush().File.Id.ShouldBeGreaterThan(0);
            await tx.CommitAsync(ct);
        }
        var gate = new AssetGate();
        await using var collectorDb = _database.CreateContext();
        Task? upload = null;
        Task? cleanup = null;
        Exception? primary = null;
        try
        {
            var pendingUpload = AssetHandler(publisher, Ledger(publisher, cas),
                new GateAssetCommit(new EfUnitOfWork(publisher), gate)).HandleAsync(new(Png, "image/png"), ct);
            upload = pendingUpload;
            await gate.Entered.Task.WaitAsync(ct);
            int fileId = (await publisher.Files.SingleAsync(ct)).Id;
            (await publisher.ReferenceAssets.SingleAsync(ct)).FileId.ShouldBe(fileId);
            int collectorProcess = await collectorDb.OpenBackendProcessIdAsync(ct);
            var pendingCleanup = Ledger(collectorDb, cas).DeleteIfUnreferencedAsync(fileId, ct);
            cleanup = pendingCleanup;
            (await _database.WaitsForALockAsync(collectorProcess, pendingCleanup, ct)).ShouldBeTrue();
            pendingCleanup.IsCompleted.ShouldBeFalse();
            gate.Release.TrySetResult();

            var result = await pendingUpload;
            result.IsError.ShouldBeFalse();
            (await pendingCleanup).ShouldBeFalse();
            (await collectorDb.Files.SingleAsync(ct)).Id.ShouldBe(fileId);
            (await new Romd.Infrastructure.Storage.ReadOnlyReferenceBlobStore(cas).ReadAsync(result.Value, ct)).ShouldBe(Png);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            gate.Release.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary,
                upload ?? Task.CompletedTask, cleanup ?? Task.CompletedTask);
        }
    }

    private sealed class AssetGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task HoldAsync(CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
    }

    private sealed class GateReservation(IReferenceAssetReservationStore inner, AssetGate gate) : IReferenceAssetReservationStore
    {
        public async Task AcquireAsync(CancellationToken ct) { await inner.AcquireAsync(ct); await gate.HoldAsync(ct); }
        public Task ReserveStagedAsync(Sha256 hash, int fileId, string contentType, DateTimeOffset reservedUntil, CancellationToken ct) =>
            inner.ReserveStagedAsync(hash, fileId, contentType, reservedUntil, ct);
    }

    private sealed class GateAssetCommit(IUnitOfWork inner, AssetGate gate) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), inner, gate);
        private sealed class Transaction(ITransaction transaction, IUnitOfWork work, AssetGate gate) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await work.FlushAsync(cancellationToken);
                await gate.HoldAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
