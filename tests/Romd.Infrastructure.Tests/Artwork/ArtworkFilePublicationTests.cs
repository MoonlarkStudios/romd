using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Extensions;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkFilePublicationTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"romd-artwork-publication-{Guid.NewGuid():N}");
    private static readonly Sha256 Hash = Sha256.FromSpan(SHA256.HashData("artwork"u8));
    private string BlobPath => Path.Combine(_root, $"{Hash}.raw");

    [Fact]
    public async Task DeleteIfUnreferencedAsync_BlobDeletionFails_CommitsRowDeletionLeavesOrphanAndReleasesLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(BlobPath, "artwork", ct);
        await using var cleanupDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        var original = await new FileRepository(cleanupDb).CreateAndCommitAsync(cleanupDb, FileEntity.CreateNew(Hash, 7, 7, false, now: DateTimeOffset.UnixEpoch), ct);
        var cas = CreateCas();
        cas.Configure().DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(async Task<bool> (_) =>
        {
            // The row deletion and its event committed before the blob deletion began.
            (await observer.Files.AnyAsync(x => x.Id == original.Id, ct)).ShouldBeFalse();
            (await observer.AdminRealtimeOutboxEvents.CountAsync(ct)).ShouldBe(1);
            throw new IOException("Controlled CAS deletion failure");
        });
        var service = new FileStorageService(cas, new FileRepository(cleanupDb), new EfUnitOfWork(cleanupDb),
            new FileMutationLock(cleanupDb), new AdminRealtimeOutbox(cleanupDb, TimeProvider.System),
            NullLogger<FileStorageService>.Instance, clock: TimeProvider.System);

        (await service.DeleteIfUnreferencedAsync(original.Id, ct)).ShouldBeTrue();

        cleanupDb.ChangeTracker.Entries().ShouldBeEmpty();
        (await observer.Files.AnyAsync(x => x.Id == original.Id, ct)).ShouldBeFalse();
        (await observer.AdminRealtimeOutboxEvents.CountAsync(ct)).ShouldBe(1);
        // An orphan blob, never a row without its blob: the orphan sweep reclaims it.
        File.Exists(BlobPath).ShouldBeTrue();
        await using var transaction = await observer.Database.BeginTransactionAsync(ct);
        await new FileMutationLock(observer).AcquireAsync(Hash, ct);
        await transaction.CommitAsync(ct);
    }

    [Fact]
    public async Task AcquireAsync_WithoutCallerTransaction_RejectsUnscopedLock()
    {
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => new FileMutationLock(db).AcquireAsync(Hash));
    }

    [Fact]
    public async Task PruneOrphanedFilesAsync_SnapshotBeforePublication_WaitsThenPreservesRetainedBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(BlobPath, "artwork", ct);
        await using var publishDb = _database.CreateContext();
        await using var cleanupDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        await cleanupDb.Database.OpenConnectionAsync(ct);
        int cleanupPid = await cleanupDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var cas = CreateCas();
        var ledger = CreateService(publishDb, cas);
        await using var source = new MemoryStream("artwork"u8.ToArray());
        await using var staged = await ledger.StageAsync(source, ct);
        await using var transaction = await publishDb.Database.BeginTransactionAsync(ct);
        await new FileMutationLock(publishDb).AcquireAsync(Hash, ct);

        var cleanup = CreateService(cleanupDb, cas).PruneOrphanedFilesAsync(ct);
        await WaitForAdvisoryWaitAsync(observer, cleanupPid, ct);
        cleanup.IsCompleted.ShouldBeFalse();
        int retainedFile = await PublishAsync(publishDb, ledger, staged, ct);
        await transaction.CommitAsync(ct);

        (await cleanup).ShouldBe(0);
        File.Exists(BlobPath).ShouldBeTrue();
        await cas.DidNotReceive().DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
        (await new FileRepository(observer).IsReferencedAsync(retainedFile, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteIfUnreferencedAsync_DeletionInProgress_BlocksPublicationUntilCommitThenRestoresBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(BlobPath, "artwork", ct);
        await using var seedDb = _database.CreateContext();
        var initial = await new FileRepository(seedDb).CreateAndCommitAsync(seedDb, FileEntity.CreateNew(Hash, 7, 7, false, now: DateTimeOffset.UnixEpoch), ct);
        await using var cleanupDb = _database.CreateContext();
        await using var publishDb = _database.CreateContext();
        await using var observer = _database.CreateContext();
        await publishDb.Database.OpenConnectionAsync(ct);
        int publisherPid = await publishDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cas = CreateCas();
        cas.Configure().DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            File.Delete(BlobPath);
            deleting.TrySetResult();
            await releaseDelete.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            return true;
        });
        var cleanup = CreateService(cleanupDb, cas).DeleteIfUnreferencedAsync(initial.Id, ct);
        await deleting.Task.WaitAsync(ct);
        var publication = PublishAfterLockAsync(publishDb, cas, ct);
        try
        {
            await WaitForAdvisoryWaitAsync(observer, publisherPid, ct);
            publication.IsCompleted.ShouldBeFalse();
            File.Exists(BlobPath).ShouldBeFalse();
            // The row deletion committed first; the blob deletion holds the lock on its own.
            (await observer.Files.AnyAsync(x => x.Id == initial.Id, ct)).ShouldBeFalse();
        }
        finally
        {
            releaseDelete.TrySetResult();
        }
        (await cleanup).ShouldBeTrue();
        int publishedId = await publication;
        publishedId.ShouldNotBe(initial.Id);
        (await observer.Files.AnyAsync(x => x.Id == initial.Id, ct)).ShouldBeFalse();
        (await new FileRepository(observer).IsReferencedAsync(publishedId, ct)).ShouldBeTrue();
        File.Exists(BlobPath).ShouldBeTrue();
        (await File.ReadAllTextAsync(BlobPath, ct)).ShouldBe("artwork");
    }

    private static async Task WaitForAdvisoryWaitAsync(RomdDbContext observer, int pid, CancellationToken ct)
    {
        // Observe PostgreSQL's actual lock queue instead of assuming scheduling from elapsed time.
        while (!await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {pid} AND locktype = 'advisory' AND NOT granted) AS \"Value\"").SingleAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private async Task<int> PublishAfterLockAsync(RomdDbContext db, IContentAddressableStore cas, CancellationToken ct)
    {
        var ledger = CreateService(db, cas);
        await using var source = new MemoryStream("artwork"u8.ToArray());
        await using var staged = await ledger.StageAsync(source, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await new FileMutationLock(db).AcquireAsync(Hash, ct);
        int id = await PublishAsync(db, ledger, staged, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>Publishes through the ledger, as artwork does, then references the file from an asset.</summary>
    private async Task<int> PublishAsync(RomdDbContext db, IFileStorageService ledger, IStagedFile staged, CancellationToken ct)
    {
        var file = (await ledger.PublishAndFlushAsync(db, staged, ct)).File;
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = now, CreatedByUserId = user });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = "None",
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = now,
            CreatedByUserId = user
        });
        db.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            TitleId = 1,
            Role = ArtworkRole.Poster,
            SourceId = "steamgriddb",
            ProviderGameId = "1",
            ProviderAssetId = "2",
            OriginalFileId = file.Id,
            ContentVersion = "version",
            ContentType = "image/png",
            Width = 600,
            Height = 900,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return file.Id;
    }

    private IContentAddressableStore CreateCas()
    {
        var cas = Substitute.For<IContentAddressableStore>();
        cas.StageAsync(Arg.Any<Stream>(), Arg.Any<StorageKey?>(), Arg.Any<IProgress<StoreProgress>?>(),
            Arg.Any<CancellationToken>()).Returns(async call =>
        {
            // Real staging consumes and snapshots its input before publication; the snapshot owns its bytes.
            using var snapshot = new MemoryStream();
            await call.ArgAt<Stream>(0).CopyToAsync(snapshot, call.ArgAt<CancellationToken>(3));
            byte[] bytes = snapshot.ToArray();
            var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));
            var staged = Substitute.For<IStagedContent>();
            staged.Key.Returns(key);
            staged.Size.Returns((long)bytes.Length);
            staged.PublishAsync(Arg.Any<CancellationToken>()).Returns(async publish =>
            {
                await File.WriteAllBytesAsync(BlobPath, bytes, publish.ArgAt<CancellationToken>(0));
                return new StoreResult(key, bytes.Length, bytes.Length, false, false);
            });
            return staged;
        });
        cas.DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(_ => { File.Delete(BlobPath); return Task.FromResult(true); });
        cas.EnumerateAsync(Arg.Any<CancellationToken>()).Returns(_ => ListBlobAsync());
        return cas;
    }

    private async IAsyncEnumerable<StorageKey> ListBlobAsync()
    {
        if (File.Exists(BlobPath))
            yield return StorageKey.FromHash(Hash);
        await Task.CompletedTask;
    }

    private FileStorageService CreateService(RomdDbContext db, IContentAddressableStore cas) => new(cas,
        new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db), Substitute.For<IAdminEventOutbox>(),
        NullLogger<FileStorageService>.Instance, TimeProvider.System);

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
