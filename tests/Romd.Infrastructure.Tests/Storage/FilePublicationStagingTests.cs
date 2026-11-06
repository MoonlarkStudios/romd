using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Configuration;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class FilePublicationStagingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddTicks(1234567);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-file-staging", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private readonly Guid _revision = Guid.NewGuid();
    private IContentAddressableStore Cas => _provider.GetRequiredService<IContentAddressableStore>();

    public FilePublicationStagingTests()
    {
        _provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = Path.Combine(_root, "cas"))
            .BuildServiceProvider();
        using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "File tests",
            ShortName = "file-tests",
            CanonicalKey = "file-tests",
            BaseName = "File tests",
            BaseCompactLabel = "File tests",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 7,
            Name = "Media",
            NormalizedName = "media",
            EnrichmentStatus = "None",
            Revision = _revision,
            CreatedAt = Now
        });
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _database.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PublishStaged_BatchRepeatedHash_PreservesOrderFlagsPendingIdentityAndUnrelatedTracking()
    {
        await using var db = _database.CreateContext();
        var hashes = new List<Sha256>();
        var service = Storage(db, new RecordingLock(new FileMutationLock(db), hashes));
        await using var first = await StageAsync(service, [1, 2, 3]);
        await using var second = await StageAsync(service, [4, 5, 6]);
        await using var repeated = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 7);
        platform.Name = "Before publication";

        var publication = await service.PublishStagedAsync([first.File, second.File, repeated.File]);

        publication.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.Platforms.AsNoTracking().SingleAsync(row => row.Id == 7)).Name.ShouldBe("File tests");
        hashes.ShouldBe(new[] { first.File.Sha256, second.File.Sha256 }.OrderBy(hash => hash.ToString(), StringComparer.Ordinal));
        await work.FlushAsync();
        publication.RequiresFlush.ShouldBeFalse();
        var results = publication.ReadAfterFlush();
        results.Select(result => result.File.Sha256).ShouldBe([first.File.Sha256, second.File.Sha256, first.File.Sha256]);
        results.Select(result => result.WasNew).ShouldBe([true, true, false]);
        results[2].WasDeduplicated.ShouldBeTrue();
        results[0].File.Id.ShouldBe(results[2].File.Id);
        results[1].File.Id.ShouldNotBe(results[0].File.Id);
        results.ShouldAllBe(result => result.File.Id > 0);
        platform.Name = "After publication";
        await using (var outside = _database.CreateContext())
            (await outside.Files.CountAsync()).ShouldBe(0);
        await transaction.CommitAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(2);
        (await verification.Platforms.SingleAsync(row => row.Id == 7)).Name.ShouldBe("After publication");
        (await verification.Files.ToListAsync()).ShouldAllBe(row => row.CreatedAt == DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task PublishStaged_RequiresCallerBoundary_AndCreationGuardsRejectBeforeTracking()
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var staged = await StageAsync(service, [1, 2, 3]);
        await Should.ThrowAsync<InvalidOperationException>(() => service.PublishStagedAsync(staged.File));
        await Should.ThrowAsync<InvalidOperationException>(() => service.PublishStagedAsync(Array.Empty<IStagedFile>()));
        (await Cas.ExistsAsync(StorageKey.FromHash(staged.File.Sha256))).ShouldBeFalse();
        var repository = new FileRepository(db);
        var file = FileEntity.CreateNew(staged.File.Sha256, 3, 3, false, Now);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddStagedAsync(file, default));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => repository.AddStagedAsync(file, cancellation.Token));
        cancelled.CancellationToken.ShouldBe(cancellation.Token);
        (await Should.ThrowAsync<OperationCanceledException>(() => service.PublishStagedAsync(Array.Empty<IStagedFile>(), cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(
            FileEntity.Rehydrate(42, file.Sha256, 3, 3, false, Now), default));
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_ExistingFile_DoesNotFlushUnrelatedStagingEvenWhenCapturingChecksums(bool unknownChecksums)
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var staged = await StageAsync(service, [1, 2, 3]);
        await using var content = new MemoryStream([1, 2, 3]);
        var stored = await Cas.StoreAsync(content);
        var work = new EfUnitOfWork(db);
        int id;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            await new FileMutationLock(db).AcquireAsync(stored.Key.Hash);
            id = (await new FileRepository(db).CreateAndFlushAsync(db, FileEntity.CreateNew(stored.Key.Hash, stored.Size, stored.CompressedSize,
                stored.IsCompressed, Now, unknownChecksums ? null : staged.File.ArtifactMd5,
                unknownChecksums ? null : staged.File.ArtifactSha1), default)).Id;
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var seeded = await db.Files.SingleAsync(row => row.Id == id);
        seeded.ArtifactMd5.ShouldBe(unknownChecksums ? null : staged.File.ArtifactMd5);
        seeded.ArtifactSha1.ShouldBe(unknownChecksums ? null : staged.File.ArtifactSha1);
        await using var repeated = await StageAsync(service, [1, 2, 3]);
        IStagedFilePublication publication;
        await using (var next = await work.BeginTransactionAsync())
        {
            var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 7);
            platform.Name = "Must remain staged";

            publication = await service.PublishStagedAsync(repeated.File);

            publication.RequiresFlush.ShouldBeFalse();
            var result = publication.ReadAfterFlush();
            result.File.Id.ShouldBe(id);
            result.WasNew.ShouldBeFalse();
            result.WasDeduplicated.ShouldBeTrue();
            result.File.ArtifactMd5.ShouldBe(repeated.File.ArtifactMd5);
            result.File.ArtifactSha1.ShouldBe(repeated.File.ArtifactSha1);
            var captured = await db.Files.AsNoTracking().SingleAsync(row => row.Id == id);
            captured.ArtifactMd5.ShouldBe(repeated.File.ArtifactMd5);
            captured.ArtifactSha1.ShouldBe(repeated.File.ArtifactSha1);
            (await db.Platforms.AsNoTracking().SingleAsync(row => row.Id == 7)).Name.ShouldBe("File tests");
            db.Entry(platform).State.ShouldBe(EntityState.Modified);
            await next.RollbackAsync();
        }
        RejectCompletedPublication(publication);
        await using var replacement = await work.BeginTransactionAsync();
        RejectCompletedPublication(publication);
        var rolledBack = await db.Files.SingleAsync(row => row.Id == id);
        rolledBack.ArtifactMd5.ShouldBe(seeded.ArtifactMd5);
        rolledBack.ArtifactSha1.ShouldBe(seeded.ArtifactSha1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_PostFlushDeduplication_AllReceiptsExpireWithOriginalTransaction(bool commit)
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var first = await StageAsync(service, [1, 2, 3]);
        await using var second = await StageAsync(service, [1, 2, 3]);
        await using var third = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(db);
        IStagedFilePublication creation;
        IStagedFilePublication deduplication;
        IStagedFilePublicationBatch batch;
        IStagedFilePublicationBatch empty;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            creation = await service.PublishStagedAsync(first.File);
            await work.FlushAsync();
            deduplication = await service.PublishStagedAsync(second.File);
            batch = await service.PublishStagedAsync([third.File]);
            empty = await service.PublishStagedAsync(Array.Empty<IStagedFile>());
            var created = creation.ReadAfterFlush();
            created.WasNew.ShouldBeTrue();
            deduplication.RequiresFlush.ShouldBeFalse();
            var reused = deduplication.ReadAfterFlush();
            reused.File.Id.ShouldBe(created.File.Id);
            reused.WasNew.ShouldBeFalse();
            reused.WasDeduplicated.ShouldBeTrue();
            batch.RequiresFlush.ShouldBeFalse();
            batch.ReadAfterFlush().Single().File.Id.ShouldBe(created.File.Id);
            empty.RequiresFlush.ShouldBeFalse();
            empty.ReadAfterFlush().ShouldBeEmpty();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        RejectCompletedPublication(creation);
        RejectCompletedPublication(deduplication);
        RejectCompletedPublication(batch);
        RejectCompletedPublication(empty);
        await using var replacement = await work.BeginTransactionAsync();
        RejectCompletedPublication(creation);
        RejectCompletedPublication(deduplication);
        RejectCompletedPublication(batch);
        RejectCompletedPublication(empty);
        (await db.Files.CountAsync()).ShouldBe(commit ? 1 : 0);
    }

    private static void RejectCompletedPublication(IStagedFilePublication publication)
    {
        Should.Throw<InvalidOperationException>(() => publication.RequiresFlush);
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
    }

    private static void RejectCompletedPublication(IStagedFilePublicationBatch publication)
    {
        Should.Throw<InvalidOperationException>(() => publication.RequiresFlush);
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_RepeatedPublication_ReusesCanonicalBeforeOrAfterOwnerFlush(bool flushBeforeRepeat)
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var first = await StageAsync(service, [1, 2, 3]);
        await using var repeated = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var initial = await service.PublishStagedAsync(first.File);
        (await db.Files.CountAsync()).ShouldBe(0);
        if (flushBeforeRepeat)
            await work.FlushAsync();
        var next = await service.PublishStagedAsync(repeated.File);

        initial.RequiresFlush.ShouldBe(!flushBeforeRepeat);
        next.RequiresFlush.ShouldBe(!flushBeforeRepeat);
        if (!flushBeforeRepeat)
        {
            Should.Throw<InvalidOperationException>(() => initial.ReadAfterFlush());
            Should.Throw<InvalidOperationException>(() => next.ReadAfterFlush());
        }
        await work.FlushAsync();
        var created = initial.ReadAfterFlush();
        var reused = next.ReadAfterFlush();
        created.File.Id.ShouldBe(reused.File.Id);
        created.WasNew.ShouldBeTrue();
        reused.WasNew.ShouldBeFalse();
        reused.WasDeduplicated.ShouldBeTrue();
        (await db.Files.CountAsync()).ShouldBe(1);
        await transaction.CommitAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task PublishStaged_RollbackThenSameContextRetry_DoesNotReuseFailedIdentityOrLeakStaging()
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var first = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(db);
        IStagedFilePublication failed;
        int failedId;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            failed = await service.PublishStagedAsync(first.File);
            await work.FlushAsync();
            failedId = failed.ReadAfterFlush().File.Id;
            await transaction.RollbackAsync();
            await work.FlushAsync();
            db.ChangeTracker.Entries().ShouldBeEmpty();
            Should.Throw<InvalidOperationException>(() => failed.ReadAfterFlush());
        }
        await using var retry = await StageAsync(service, [1, 2, 3]);
        await using var next = await work.BeginTransactionAsync();
        var publication = await service.PublishStagedAsync(retry.File);
        await work.FlushAsync();
        var result = publication.ReadAfterFlush();
        result.File.Id.ShouldNotBe(failedId);
        result.WasNew.ShouldBeTrue();
        result.WasDeduplicated.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => failed.ReadAfterFlush());
        await next.CommitAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.SingleAsync()).Id.ShouldBe(result.File.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCreation_ConflictingSizeOrChecksum_RejectsWithoutFlushingOrReplacingCanonicalFacts(bool sizeConflict)
    {
        await using var db = _database.CreateContext();
        var service = Storage(db);
        await using var staged = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var publication = await service.PublishStagedAsync(staged.File);
        var conflict = FileEntity.CreateNew(staged.File.Sha256, sizeConflict ? 4 : 3, 3, false, Now,
            sizeConflict ? staged.File.ArtifactMd5 : Md5.FromBytes(new byte[Md5.ByteLength]), staged.File.ArtifactSha1);
        var repository = new FileRepository(db);

        await Should.ThrowAsync<InvalidDataException>(() => repository.AddStagedAsync(conflict, default));

        (await db.Files.CountAsync()).ShouldBe(0);
        publication.RequiresFlush.ShouldBeTrue();
        await work.FlushAsync();
        var file = publication.ReadAfterFlush().File;
        file.Size.ShouldBe(3);
        file.ArtifactMd5.ShouldBe(staged.File.ArtifactMd5);
        await transaction.CommitAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.SingleAsync()).ArtifactMd5.ShouldBe(staged.File.ArtifactMd5);
    }

    [Fact]
    public async Task PublishStaged_ConcurrentOpposingBatches_CommitsOneCanonicalRowPerHash()
    {
        async Task<int[]> PublishAsync(bool reverse)
        {
            await using var db = _database.CreateContext();
            var service = Storage(db);
            await using var first = await StageAsync(service, reverse ? [4, 5, 6] : [1, 2, 3]);
            await using var second = await StageAsync(service, reverse ? [1, 2, 3] : [4, 5, 6]);
            var work = new EfUnitOfWork(db);
            await using var transaction = await work.BeginTransactionAsync();
            var publication = await service.PublishStagedAsync([first.File, second.File]);
            if (publication.RequiresFlush)
                await work.FlushAsync();
            int[] ids = publication.ReadAfterFlush().Select(result => result.File.Id).ToArray();
            await transaction.CommitAsync();
            return ids;
        }

        var results = await Task.WhenAll(PublishAsync(false), PublishAsync(true));

        results[0].ShouldBe(results[1].Reverse());
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_CollectorWaitsForPublication_ThenRechecksCommittedRow(bool commit)
    {
        await using var owner = _database.CreateContext();
        await using var collector = _database.CreateContext();
        var service = Storage(owner);
        await using var staged = await StageAsync(service, [1, 2, 3]);
        var work = new EfUnitOfWork(owner);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await work.BeginTransactionAsync(cancellation.Token);
        var publication = await service.PublishStagedAsync(staged.File, cancellation.Token);
        await work.FlushAsync(cancellation.Token);
        var (pending, waited) = await _database.StartWaitingForALockAsync(collector,
            token => Storage(collector).DeleteOrphanAsync(staged.File.Sha256, token), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            if (commit)
                await transaction.CommitAsync(cancellation.Token);
            else
                await transaction.RollbackAsync(cancellation.Token);
            (await pending).ShouldBe(!commit);
            (await Cas.ExistsAsync(StorageKey.FromHash(staged.File.Sha256))).ShouldBe(commit);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
    }

    [Theory]
    [InlineData(false, Failure.None)]
    [InlineData(true, Failure.None)]
    [InlineData(false, Failure.AfterFileFlush)]
    [InlineData(true, Failure.AfterFileFlush)]
    [InlineData(false, Failure.CancelAfterFileFlush)]
    [InlineData(true, Failure.CancelAfterFileFlush)]
    [InlineData(false, Failure.BeforeCommit)]
    [InlineData(true, Failure.BeforeCommit)]
    [InlineData(false, Failure.AfterFileFlushAndCleanup)]
    [InlineData(true, Failure.AfterFileFlushAndCleanup)]
    [InlineData(false, Failure.CancelAfterFileFlushAndCleanup)]
    [InlineData(true, Failure.CancelAfterFileFlushAndCleanup)]
    [InlineData(false, Failure.BeforeCommitAndCleanup)]
    [InlineData(true, Failure.BeforeCommitAndCleanup)]
    [InlineData(false, Failure.LostCommitAckAndCleanup)]
    [InlineData(true, Failure.LostCommitAckAndCleanup)]
    [InlineData(false, Failure.AcknowledgedCleanup)]
    [InlineData(true, Failure.AcknowledgedCleanup)]
    public async Task MediaOwner_FileIdentityFlushAndReferenceCommit_AreAtomic(bool gallery, Failure failure)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptFilePublication(new EfUnitOfWork(db), db, cancellation, failure);
        var storage = Storage(db);
        async Task InvokeAsync()
        {
            if (gallery)
                (await Gallery(db, storage, work).ImportAsync(7, Guid.Empty, ArtworkCandidateIds.Of("https://images.igdb.com/media.png"),
                    "reference", RevisionPrecondition.For(_revision.ToString()), cancellation.Token)).IsError.ShouldBeFalse();
            else
            {
                var options = Substitute.For<IRomdOptions>();
                options.DataDirectory.Returns(_root);
                options.MaxUploadBytes.Returns(1L << 20);
                var titles = new TitleRepository(db);
                var reads = new TitleReadRepository(db);
                var handler = new UploadTitleMediaCommandHandler(titles, titles, reads, reads, storage,
                    new LocalTempFileFactory(options, new System.IO.Abstractions.FileSystem()), new AdminRealtimeOutbox(db, TimeProvider.System), work,
                    NullLogger<UploadTitleMediaCommandHandler>.Instance, TimeProvider.System);
                (await handler.HandleAsync(new UploadTitleMediaCommand(7, MediaType.Screenshot, new MemoryStream(Png), "media.png",
                    RevisionPrecondition.For(_revision.ToString())), cancellation.Token)).IsError.ShouldBeFalse();
            }
        }
        if (failure is Failure.CancelAfterFileFlush or Failure.CancelAfterFileFlushAndCleanup)
        {
            Exception? observed = null;
            try
            { await InvokeAsync(); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(work.CancellationFailure);
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        }
        else if (failure is not (Failure.None or Failure.AcknowledgedCleanup))
            (await Should.ThrowAsync<IOException>(InvokeAsync)).ShouldBeSameAs(work.Primary);
        else
            await InvokeAsync();

        work.SawFileBeforeReferences.ShouldBeTrue();
        if (failure is Failure.BeforeCommit or Failure.BeforeCommitAndCleanup)
            work.SawReferenceAndOutboxBeforeCommit.ShouldBeTrue();
        work.Disposals.ShouldBe(1);
        work.CommitAttempts.ShouldBe(failure is Failure.AfterFileFlush or Failure.AfterFileFlushAndCleanup or Failure.CancelAfterFileFlush or Failure.CancelAfterFileFlushAndCleanup ? 0 : 1);
        bool committed = failure is Failure.None or Failure.AcknowledgedCleanup or Failure.LostCommitAckAndCleanup;
        if (!committed)
            db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = _database.CreateContext();
        (await verification.Files.CountAsync()).ShouldBe(committed ? 1 : 0);
        (await verification.TitleMedia.CountAsync()).ShouldBe(committed ? 1 : 0);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(committed ? 1 : 0);
        var revision = (await verification.Titles.SingleAsync()).Revision;
        if (committed)
            revision.ShouldNotBe(_revision);
        else
            revision.ShouldBe(_revision);
        if (committed)
        {
            var file = await verification.Files.SingleAsync();
            (await verification.TitleMedia.SingleAsync()).FileId.ShouldBe(file.Id);
            (await Cas.ExistsAsync(StorageKey.FromHash(file.Sha256))).ShouldBeTrue();
        }
    }

    private GalleryArtworkService Gallery(RomdDbContext db, IFileStorageService storage, IUnitOfWork work)
    {
        var browsing = Substitute.For<IArtworkBrowsingService>();
        var candidate = new TrustedArtworkCandidate("igdb", "42", "asset", ArtworkRole.Hero, "https://images.igdb.com/media.png",
            "https://images.igdb.com/preview.png", null, "https://www.igdb.com/games/test", MediaType.Screenshot);
        browsing.ValidateCandidateAsync(7, Guid.Empty, "reference", Arg.Any<CancellationToken>()).Returns(ErrorOr.ErrorOrFactory.From(candidate));
        var source = Substitute.For<IArtworkAssetSource>();
        source.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ArtworkRole>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadedArtworkAsset(Png, "image/png", null, candidate.SourcePageUrl));
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/png", 640, 480, []));
        var titles = new TitleRepository(db);
        var reads = new TitleReadRepository(db);
        return new(browsing, source, processor, storage, titles, titles, reads, reads, work,
            new AdminRealtimeOutbox(db, TimeProvider.System), TimeProvider.System, NullLogger<GalleryArtworkService>.Instance);
    }

    private FileStorageService Storage(RomdDbContext db, IFileMutationLock? locks = null) => new(Cas, new FileRepository(db),
        new EfUnitOfWork(db), locks ?? new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System),
        NullLogger<FileStorageService>.Instance, new ManualTimeProvider(Now));

    private static async Task<StagedInput> StageAsync(IFileStorageService service, byte[] bytes)
    {
        var source = new MemoryStream(bytes);
        try
        { return new(await service.StageAsync(source), source); }
        catch { await source.DisposeAsync(); throw; }
    }
    private sealed class StagedInput(IStagedFile file, Stream source) : IAsyncDisposable
    {
        public IStagedFile File => file;
        public async ValueTask DisposeAsync() { await file.DisposeAsync(); await source.DisposeAsync(); }
    }
    private sealed class RecordingLock(IFileMutationLock inner, List<Sha256> hashes) : IFileMutationLock
    {
        public IFilePublicationBoundary CapturePublicationBoundary() => inner.CapturePublicationBoundary();
        public Task AcquireAsync(Sha256 hash, CancellationToken ct = default) { hashes.Add(hash); return inner.AcquireAsync(hash, ct); }
    }
    public enum Failure { None, AfterFileFlush, CancelAfterFileFlush, BeforeCommit, AfterFileFlushAndCleanup, CancelAfterFileFlushAndCleanup, BeforeCommitAndCleanup, LostCommitAckAndCleanup, AcknowledgedCleanup }
    private sealed class InterruptFilePublication(IUnitOfWork inner, RomdDbContext db, CancellationTokenSource cancellation,
        Failure failure) : IUnitOfWork
    {
        public IOException Primary { get; } = new("Injected primary publication failure.");
        public OperationCanceledException? CancellationFailure { get; private set; }
        public int Disposals { get; private set; }
        public int CommitAttempts { get; private set; }
        private Failure FailureMode => failure;
        public bool SawFileBeforeReferences { get; private set; }
        public bool SawReferenceAndOutboxBeforeCommit { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            if (!SawFileBeforeReferences && await db.Files.AnyAsync(cancellationToken) && !await db.TitleMedia.AnyAsync(cancellationToken))
            {
                SawFileBeforeReferences = true;
                if (failure is Failure.CancelAfterFileFlush or Failure.CancelAfterFileFlushAndCleanup)
                {
                    await cancellation.CancelAsync();
                    CancellationFailure = new OperationCanceledException(cancellation.Token);
                    throw CancellationFailure;
                }
                if (failure is Failure.AfterFileFlush or Failure.AfterFileFlushAndCleanup)
                    throw Primary;
            }
        }
        private async Task BeforeCommitAsync(CancellationToken ct)
        {
            if (failure is not (Failure.BeforeCommit or Failure.BeforeCommitAndCleanup))
                return;
            await inner.FlushAsync(ct);
            SawReferenceAndOutboxBeforeCommit = await db.TitleMedia.AnyAsync(ct) && await db.AdminRealtimeOutboxEvents.AnyAsync(ct);
            throw Primary;
        }
        private sealed class Transaction(InterruptFilePublication owner, ITransaction transaction) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                owner.CommitAttempts++;
                await owner.BeforeCommitAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (owner.FailureMode == Failure.LostCommitAckAndCleanup)
                    throw owner.Primary;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                owner.Disposals++;
                if (owner.FailureMode is Failure.AfterFileFlushAndCleanup or Failure.CancelAfterFileFlushAndCleanup
                    or Failure.BeforeCommitAndCleanup or Failure.LostCommitAckAndCleanup or Failure.AcknowledgedCleanup)
                    throw new IOException("Secondary publication transaction cleanup failure.");
            }
        }
    }
}
