using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.PinTitleMediaArtwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Storage;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class PinTitleMediaArtworkOwnershipTests : IAsyncLifetime
{
    private static readonly byte[] Original = [1, 2, 3];
    private static readonly byte[] Variant = [4, 5, 6];
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(123);
    private static readonly PinTitleMediaArtworkCommand Command = new(7, ArtworkRole.Hero, 7,
        RevisionPrecondition.For(ArtworkValidators.Role(0)), 0, 100);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-pin-ownership", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private int _originalId;
    private IContentAddressableStore Cas => _provider.GetRequiredService<IContentAddressableStore>();

    public PinTitleMediaArtworkOwnershipTests() => _provider = new ServiceCollection()
        .AddContentAddressableStorage(options => options.RootPath = _root).BuildServiceProvider();

    public async Task InitializeAsync()
    {
        await using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Pin tests",
            ShortName = "pin-tests",
            CanonicalKey = "pin-tests",
            BaseName = "Pin tests",
            BaseCompactLabel = "Pin tests",
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
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        _originalId = await SeedFileAsync(Original);
        db.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 7,
            TitleId = 7,
            FileId = _originalId,
            Type = "Cover",
            SourceId = "user",
            ContentType = "image/png",
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_NewOrExistingFileIdentity_FlushesBeforeGalleryAndReturnsCommittedPin(bool existingVariant)
    {
        if (existingVariant)
            await SeedFileAsync(Variant);
        await using var db = _database.CreateContext();
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db));
        work.AfterFlush = async (count, ct) =>
        {
            if (count == 1 && !existingVariant)
            {
                (await db.Files.CountAsync(ct)).ShouldBe(2);
                (await db.ArtworkAssets.CountAsync(ct)).ShouldBe(0);
            }
            if (count == (existingVariant ? 1 : 2))
            {
                var asset = await db.ArtworkAssets.Include(row => row.Variants).SingleAsync(ct);
                asset.Id.ShouldBeGreaterThan(0);
                asset.Variants.Single().FileId.ShouldBeGreaterThan(0);
                (await db.ArtworkSelections.SingleAsync(ct)).PinnedAssetId.ShouldBeNull();
            }
        };
        var result = await Handler(db, work).HandleAsync(Command);

        result.IsError.ShouldBeFalse();
        (result.Value.Mode, result.Value.Revision).ShouldBe((ArtworkSelectionMode.Pinned, 1));
        work.FlushCount.ShouldBe(existingVariant ? 2 : 3);
        work.Committed.ShouldBeTrue();
        await using var observer = _database.CreateContext();
        (await observer.Files.CountAsync()).ShouldBe(2);
        var selection = await observer.ArtworkSelections.SingleAsync();
        (selection.PinnedAssetId, selection.FocalX, selection.FocalY).ShouldBe((result.Value.PinnedAssetId, 0, 100));
        (await observer.TitleMedia.SingleAsync()).Type.ShouldBe("Cover");
        (await observer.ArtworkAssets.SingleAsync()).OriginalFileId.ShouldBe(_originalId);
        // Pinning has never emitted a separate event; publication must not invent one during this move.
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task HandleAsync_FaultOrCancellationAfterEachRealFlush_RollsBackFilesAssetPinAndOutbox(int interruptAt, bool cancel)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var fault = new IOException("Injected after pin flush.");
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db));
        work.AfterFlush = async (count, ct) =>
        {
            await AssertOriginalOnlyAsync();
            if (count != interruptAt)
                return;
            if (cancel)
            { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
            throw fault;
        };
        Exception? failure = null;
        try
        { await Handler(db, work).HandleAsync(Command, cancellation.Token); }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(fault);
        work.FlushCount.ShouldBe(interruptAt);
        work.Committed.ShouldBeFalse();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await AssertOriginalOnlyAsync();
        await using var original = await Storage(db).RetrieveByIdAsync(_originalId, default);
        using var bytes = new MemoryStream();
        await original!.CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(Original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CommitFailureOrCommittedCleanupFailure_ReportsActualDurability(bool committedCleanup)
    {
        await using var db = _database.CreateContext();
        var fault = new IOException("Injected transaction settlement fault.");
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db))
        {
            BeforeCommit = _ => committedCleanup ? Task.CompletedTask : Task.FromException(fault),
            AfterDispose = () => committedCleanup ? ValueTask.FromException(fault) : ValueTask.CompletedTask
        };

        if (committedCleanup)
        {
            var result = await Handler(db, work).HandleAsync(Command);
            result.IsError.ShouldBeFalse();
            work.Committed.ShouldBeTrue();
            await using var observer = _database.CreateContext();
            (await observer.ArtworkSelections.SingleAsync()).PinnedAssetId.ShouldBe(result.Value.PinnedAssetId);
        }
        else
        {
            var failure = await Should.ThrowAsync<IOException>(() => Handler(db, work).HandleAsync(Command));
            failure.ShouldBeSameAs(fault);
            await AssertOriginalOnlyAsync();
            db.ChangeTracker.Entries().ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("delete")]
    [InlineData("replace")]
    public async Task HandleAsync_WinnerDuringPreparation_RechecksRevisionAndExactMediaFileBeforePublishing(string winner)
    {
        await using var db = _database.CreateContext();
        var processor = Processor(db);
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                db.Database.CurrentTransaction.ShouldBeNull();
                await using var competing = _database.CreateContext();
                var work = new EfUnitOfWork(competing);
                await using var transaction = await work.BeginTransactionAsync();
                var repository = new ArtworkCurationRepository(competing, TimeProvider.System);
                if (winner == "selection")
                    (await repository.StageEmptyAsync(7, ArtworkRole.Hero, Command.IfMatch, default)).IsError.ShouldBeFalse();
                else
                {
                    (await repository.LockSelectionAsync(7, ArtworkRole.Hero, Command.IfMatch, default)).IsError.ShouldBeFalse();
                    if (winner == "delete")
                        await competing.TitleMedia.Where(row => row.Id == 7).ExecuteDeleteAsync();
                    else
                    {
                        var replacement = await SeedFileAsync([9, 8, 7]);
                        await competing.TitleMedia.Where(row => row.Id == 7).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FileId, replacement));
                    }
                }
                await transaction.CommitAsync();
                return ErrorOrFactory.From(Image());
            });

        var result = await Handler(db, new EfUnitOfWork(db), processor).HandleAsync(Command);

        result.FirstError.Code.ShouldBe(winner == "selection" ? "Artwork.PreconditionFailed" : "Artwork.SelectionChanged");
        await using var observer = _database.CreateContext();
        (await observer.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await observer.ArtworkVariants.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await observer.Files.CountAsync()).ShouldBe(winner == "replace" ? 2 : 1);
        var selection = await observer.ArtworkSelections.SingleAsync();
        (selection.Mode, selection.Revision).ShouldBe(winner == "selection" ? (ArtworkSelectionMode.Empty, 1L) : (ArtworkSelectionMode.Automatic, 0L));
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("empty")]
    [InlineData("automatic")]
    public async Task HandleAsync_CompetingRoleWriterWaitsForPinCommit_ThenRejectsItsStaleETag(string competitor)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new ObservedUnitOfWork(new EfUnitOfWork(first))
        {
            AfterFlush = async (count, ct) =>
            {
                if (count != 1)
                    return;
                flushed.SetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var operations = new List<Task>();
        Exception? failure = null;
        try
        {
            var pin = Handler(first, work).HandleAsync(Command, cancellation.Token);
            operations.Add(pin);
            await flushed.Task.WaitAsync(cancellation.Token);
            var service = new ArtworkCurationService(new ArtworkCurationRepository(second, TimeProvider.System), new EfUnitOfWork(second), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkCurationService>.Instance);
            var (pending, waited) = await _database.StartWaitingForALockAsync(second, ct => competitor switch
            {
                "pin" => Handler(second, new EfUnitOfWork(second)).HandleAsync(Command with { FocalX = 50, FocalY = 50 }, ct),
                "empty" => service.LeaveEmptyAsync(7, ArtworkRole.Hero, Command.IfMatch, ct),
                _ => service.ReturnToAutomaticAsync(7, ArtworkRole.Hero, Command.IfMatch, ct)
            }, cancellation.Token);
            operations.Add(pending);
            waited.ShouldBeTrue();
            await AssertOriginalOnlyAsync();
            release.SetResult();
            (await pin).IsError.ShouldBeFalse();
            (await pending).FirstError.Code.ShouldBe("Artwork.PreconditionFailed");
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operations.ToArray()); }

        await using var observer = _database.CreateContext();
        var selection = await observer.ArtworkSelections.SingleAsync();
        (selection.Mode, selection.Revision, selection.FocalX, selection.FocalY).ShouldBe((ArtworkSelectionMode.Pinned, 1L, 0, 100));
        (await observer.ArtworkAssets.CountAsync()).ShouldBe(1);
        (await observer.ArtworkVariants.CountAsync()).ShouldBe(1);
        (await observer.Files.CountAsync()).ShouldBe(2);
    }

    private PinTitleMediaArtworkCommandHandler Handler(RomdDbContext db, IUnitOfWork work, IArtworkImageProcessor? processor = null) =>
        new(new ArtworkCurationRepository(db, TimeProvider.System), processor ?? Processor(db), Storage(db), work,
            NullLogger<PinTitleMediaArtworkCommandHandler>.Instance);

    private IArtworkImageProcessor Processor(RomdDbContext db)
    {
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            return ErrorOrFactory.From(Image());
        });
        return processor;
    }

    private static ProcessedArtworkImage Image() => new("image/png", 800, 400, [new("hero", "image/webp", 400, 200, Variant)]);
    private FileStorageService Storage(RomdDbContext db) => new(Cas, new FileRepository(db), new EfUnitOfWork(db),
        new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance,
        TimeProvider.System);

    private async Task<int> SeedFileAsync(byte[] bytes)
    {
        await using var source = new MemoryStream(bytes);
        var stored = await Cas.StoreAsync(source);
        await using var db = _database.CreateContext();
        return (await new FileRepository(db).CreateAndCommitAsync(db, FileEntity.CreateNew(stored.Key.Hash, stored.Size, stored.StoredSize,
            stored.IsCompressed, Now), default)).Id;
    }

    private async Task AssertOriginalOnlyAsync()
    {
        await using var observer = _database.CreateContext();
        (await observer.Files.CountAsync()).ShouldBe(1);
        (await observer.TitleMedia.SingleAsync()).FileId.ShouldBe(_originalId);
        (await observer.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await observer.ArtworkVariants.CountAsync()).ShouldBe(0);
        (await observer.ArtworkSelections.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    private sealed class ObservedUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Func<int, CancellationToken, Task>? AfterFlush { get; set; }
        public Func<CancellationToken, Task>? BeforeCommit { get; init; }
        public Func<ValueTask>? AfterDispose { get; init; }
        public int FlushCount { get; private set; }
        public bool Committed { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            FlushCount++;
            if (AfterFlush is not null)
                await AfterFlush(FlushCount, cancellationToken);
        }
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        private sealed class Transaction(ObservedUnitOfWork owner, ITransaction inner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (owner.BeforeCommit is not null)
                    await owner.BeforeCommit(cancellationToken);
                await inner.CommitAsync(cancellationToken);
                owner.Committed = true;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (owner.AfterDispose is not null)
                    await owner.AfterDispose();
            }
        }
    }
}
