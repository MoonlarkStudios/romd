using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.DeleteDat;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Infrastructure.Storage;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

/// <summary>
///     Focused real-PostgreSQL proofs for #85 Slice B mutation/outbox atomicity.
///     The injected unit of work flushes through the production DbContext, then
///     rolls the real PostgreSQL transaction back before surfacing a commit failure.
/// </summary>
public sealed partial class AdminMutationOutboxAtomicityTests
{
    private static readonly Sha1 Sha1One = Sha1.Parse("1111111111111111111111111111111111111111");
    private static readonly Sha1 Sha1Two = Sha1.Parse("2222222222222222222222222222222222222222");
    private static readonly Sha1 Sha1Three = Sha1.Parse("3333333333333333333333333333333333333333");
    private static readonly Md5 Md5One = Md5.Parse("11111111111111111111111111111111");
    private static readonly Md5 Md5Two = Md5.Parse("22222222222222222222222222222222");
    private static readonly Md5 Md5Three = Md5.Parse("33333333333333333333333333333333");
    private static readonly Crc32 CrcOne = Crc32.Parse("11111111");
    private static readonly Crc32 CrcTwo = Crc32.Parse("22222222");
    private static readonly Crc32 CrcThree = Crc32.Parse("33333333");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDat_CommitOutcome_KeepsDeletionDirtyStateLibraryFlagsAndEventsAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedLibraryAsync(database.Context, libraryId: 3);

        var handler = NewDeleteDatHandler(database, database.UnitOfWork);

        var result = await handler.HandleAsync(DeleteDatCommand.Create(7).Value);

        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AnyAsync(row => row.Id == 7)).ShouldBe(failCommit);
        // Deleting the source's last version removes the source anchor in the same commit;
        // a rolled-back delete keeps the anchor.
        (await readContext.DatSources.AnyAsync()).ShouldBe(failCommit);
        // CAS destruction is removed from the delete path; the stored source file row survives
        // either outcome and is reclaimed by the recurring orphan cleanup.
        (await readContext.Files.AnyAsync(row => row.Id == 17)).ShouldBeTrue();
        var platform = await readContext.Platforms.SingleAsync(row => row.Id == 10);
        var library = await readContext.Libraries.SingleAsync(row => row.Id == 3);

        if (failCommit)
        {
            result.IsError.ShouldBeTrue();
            platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
            library.NeedsMaterialization.ShouldBeFalse();
            (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
        }
        else
        {
            result.IsError.ShouldBeFalse();
            // No request-lifetime rebuild: the platform stays durably Dirty for the worker
            // catalog projection recovery dispatcher.
            platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
            library.NeedsMaterialization.ShouldBeTrue();
            (await GetEventTypesAsync(readContext)).ShouldBe(StatsEventTypes());
        }
    }

    [Fact]
    public async Task DeleteDat_CancelledAfterFinalFlush_RollsBackDeletionDirtyStateLibraryFlagsAndEvents()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedLibraryAsync(database.Context, libraryId: 3);
        using var cancellation = new CancellationTokenSource();

        var handler = NewDeleteDatHandler(
            database,
            new CancelAfterCommitFlushUnitOfWork(database.UnitOfWork, cancellation));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(DeleteDatCommand.Create(7).Value, cancellation.Token));

        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AnyAsync(row => row.Id == 7)).ShouldBeTrue();
        (await readContext.DatSources.AnyAsync()).ShouldBeTrue();
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await readContext.Libraries.SingleAsync(row => row.Id == 3))
            .NeedsMaterialization.ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteDat_TransactionDisposalFailsAfterCommit_StillReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedLibraryAsync(database.Context, libraryId: 3);

        var handler = NewDeleteDatHandler(
            database,
            new ThrowOnDisposeAfterCommitUnitOfWork(database.UnitOfWork));

        var result = await handler.HandleAsync(DeleteDatCommand.Create(7).Value);

        result.IsError.ShouldBeFalse();
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AnyAsync(row => row.Id == 7)).ShouldBeFalse();
        (await readContext.DatSources.AnyAsync()).ShouldBeFalse();
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await readContext.Libraries.SingleAsync(row => row.Id == 3))
            .NeedsMaterialization.ShouldBeTrue();
        (await GetEventTypesAsync(readContext)).ShouldBe(StatsEventTypes());
    }

    [Fact]
    public async Task DeleteDat_SourceHasAnotherVersion_KeepsSourceAnchor()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        database.Context.DatFiles.Add(new DatFileEntity
        {
            Id = 8,
            Name = "Superseded DAT",
            Description = "Superseded DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = 10,
            OriginalFilename = "superseded.dat",
            FileId = 17,
            DatSourceId = sourceId,
            Lifecycle = nameof(DatFileLifecycle.Superseded),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var handler = NewDeleteDatHandler(database, database.UnitOfWork);
        var result = await handler.HandleAsync(DeleteDatCommand.Create(7).Value);

        result.IsError.ShouldBeFalse();
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AnyAsync(row => row.Id == 7)).ShouldBeFalse();
        (await readContext.DatFiles.AnyAsync(row => row.Id == 8)).ShouldBeTrue();
        (await readContext.DatSources.AnyAsync(row => row.Id == sourceId)).ShouldBeTrue();
    }

    private static DeleteDatCommandHandler NewDeleteDatHandler(
        TestDatabase database,
        IUnitOfWork unitOfWork) =>
        new(
            new DatRepository(database.Context, TimeProvider.System),
            new SourceLifecycleStore(database.Context),
            new TitleRepository(database.Context),
            CatalogProjectionTestFactory.Create(database.Context),
            new LibraryRepository(database.Context),
            database.Outbox,
            unitOfWork,
            NullLogger<DeleteDatCommandHandler>.Instance);

    [Fact]
    public async Task BatchDelete_MiddleCommitFails_PreservesBothSuccessfulMutationsWithoutStaleEvents()
    {
        await using var database = await TestDatabase.CreateAsync(failCommitNumber: 2);
        await SeedFileAndRomAsync(database.Context, romId: 1, fileId: 11, Sha1One, Md5One, CrcOne);
        await SeedFileAndRomAsync(database.Context, romId: 2, fileId: 12, Sha1Two, Md5Two, CrcTwo);
        await SeedFileAndRomAsync(database.Context, romId: 3, fileId: 13, Sha1Three, Md5Three, CrcThree);

        var ownershipImpact = Substitute.For<IRomOwnershipImpactReader>();
        ownershipImpact.ReadTitlePlatformIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var fileStorage = Substitute.For<IFileStorageService>();
        var libraries = Substitute.For<ILibraryRepository>();

        var handler = new BatchDeleteRomsCommandHandler(
            new RomRepository(database.Context),
            ownershipImpact,
            Substitute.For<IRomPayloadAssertionImpactReader>(),
            fileStorage,
            database.Outbox,
            database.UnitOfWork,
            libraries,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<BatchDeleteRomsCommandHandler>.Instance);

        var result = await handler.HandleAsync(new BatchDeleteRomsCommand { RomIds = [1, 2, 3] });

        result.IsError.ShouldBeFalse();
        result.Value.DeletedCount.ShouldBe(2);
        result.Value.FailedCount.ShouldBe(1);
        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using var readContext = database.CreateReadContext();
        (await readContext.RomFiles.AnyAsync(row => row.Id == 1)).ShouldBeFalse();
        (await readContext.RomFiles.AnyAsync(row => row.Id == 2)).ShouldBeTrue();
        (await readContext.RomFiles.AnyAsync(row => row.Id == 3)).ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBe(StatsEventTypes().Concat(StatsEventTypes()).ToArray());
        await fileStorage.Received(1).DeleteIfUnreferencedAsync(11, Arg.Any<CancellationToken>());
        await fileStorage.DidNotReceive().DeleteIfUnreferencedAsync(12, Arg.Any<CancellationToken>());
        await fileStorage.Received(1).DeleteIfUnreferencedAsync(13, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeDeletedBranch_CommitOutcome_KeepsDeletedRomsAndAggregateEventsAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedFileAndRomAsync(database.Context, romId: 3, fileId: 13, Sha1One, Md5One, CrcOne);

        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.PruneUnreferencedFilesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var handler = new PurgeUnidentifiedRomsCommandHandler(
            new RomRepository(database.Context),
            fileStorage,
            database.Outbox,
            database.UnitOfWork,
            NullLogger<PurgeUnidentifiedRomsCommandHandler>.Instance);

        if (failCommit)
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                handler.HandleAsync(new PurgeUnidentifiedRomsCommand()));
        }
        else
        {
            var result = await handler.HandleAsync(new PurgeUnidentifiedRomsCommand());
            result.IsError.ShouldBeFalse();
            result.Value.DeletedCount.ShouldBe(1);
        }

        await using var readContext = database.CreateReadContext();
        (await readContext.RomFiles.AnyAsync(row => row.Id == 3)).ShouldBe(failCommit);
        (await GetEventTypesAsync(readContext)).ShouldBe(
            failCommit ? [] : StatsEventTypes());

        if (failCommit)
        {
            await fileStorage.DidNotReceive()
                .PruneUnreferencedFilesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await fileStorage.Received(1)
                .PruneUnreferencedFilesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task PurgeReclaimedOnlyBranch_FileDeletionCommitsStorageEventWithoutCoverageOrHealth()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedFileAsync(database.Context, fileId: 21, createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        var cas = Substitute.For<IContentAddressableStore>();
        cas.DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(true);
        var storage = CreateFileStorage(database, cas);
        var handler = new PurgeUnidentifiedRomsCommandHandler(
            new RomRepository(database.Context),
            storage,
            database.Outbox,
            database.UnitOfWork,
            NullLogger<PurgeUnidentifiedRomsCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PurgeUnidentifiedRomsCommand());

        result.IsError.ShouldBeFalse();
        result.Value.DeletedCount.ShouldBe(0);
        result.Value.ReclaimedFileCount.ShouldBe(1);
        await using var readContext = database.CreateReadContext();
        (await readContext.Files.AnyAsync(row => row.Id == 21)).ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBe([AdminRealtimeEventTypes.StorageStatsChanged]);
        await cas.Received(1).DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileStorageDelete_CommitFailure_RollsBackMetadataAndEventThenRetryReclaimsUnreferencedRow()
    {
        await using var database = await TestDatabase.CreateAsync(failCommitNumber: 1);
        await SeedFileAsync(database.Context, fileId: 22, createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        await SeedFileAsync(database.Context, fileId: 23, createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        var cas = Substitute.For<IContentAddressableStore>();
        var remainingBlobs = new HashSet<StorageKey>
        {
            StorageKey.FromHash(NewFile(22, DateTimeOffset.UtcNow).Sha256),
            StorageKey.FromHash(NewFile(23, DateTimeOffset.UtcNow).Sha256)
        };
        cas.DeleteAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>())
            .Returns(call => remainingBlobs.Remove(call.ArgAt<StorageKey>(0)));
        var storage = CreateFileStorage(database, cas);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            storage.DeleteIfUnreferencedAsync(22, CancellationToken.None));
        (await storage.DeleteIfUnreferencedAsync(23, CancellationToken.None)).ShouldBeTrue();

        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using var readContext = database.CreateReadContext();
        (await readContext.Files.AnyAsync(row => row.Id == 22)).ShouldBeTrue();
        (await readContext.Files.AnyAsync(row => row.Id == 23)).ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBe([AdminRealtimeEventTypes.StorageStatsChanged]);
        // CAS deletion occurs before the commit while the publication lock is held.
        // A failed commit can restore only an unreferenced metadata row, never a
        // referenced artwork pin. The next cleanup safely finishes that deletion.
        (await new FileRepository(readContext).IsReferencedAsync(22, CancellationToken.None)).ShouldBeFalse();
        remainingBlobs.ShouldBeEmpty();
        await cas.Received(1).DeleteAsync(
            StorageKey.FromHash(NewFile(22, DateTimeOffset.UtcNow).Sha256),
            Arg.Any<CancellationToken>());
        await cas.Received(1).DeleteAsync(
            StorageKey.FromHash(NewFile(23, DateTimeOffset.UtcNow).Sha256),
            Arg.Any<CancellationToken>());

        (await storage.DeleteIfUnreferencedAsync(22, CancellationToken.None)).ShouldBeTrue();
        (await readContext.Files.AnyAsync(row => row.Id == 22)).ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBe([
            AdminRealtimeEventTypes.StorageStatsChanged, AdminRealtimeEventTypes.StorageStatsChanged]);
        await cas.Received(2).DeleteAsync(
            StorageKey.FromHash(NewFile(22, DateTimeOffset.UtcNow).Sha256), Arg.Any<CancellationToken>());
    }

    private static FileStorageService CreateFileStorage(TestDatabase database, IContentAddressableStore cas) =>
        new(
            cas,
            new FileRepository(database.Context),
            database.UnitOfWork,
            new FileMutationLock(database.Context),
            database.Outbox,
            Options.Create(new ContentStoreOptions { RootPath = "/tmp/romd-slice-b-tests" }),
            NullLogger<FileStorageService>.Instance);

    private static async Task SeedPlatformFileAndDatAsync(
        RomdDbContext context,
        int datId,
        int fileId,
        int platformId)
    {
        context.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = $"Platform {platformId}",
            ShortName = $"p{platformId}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Files.Add(NewFile(fileId, DateTimeOffset.UtcNow));
        context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
            },
            Id = datId,
            Name = "Test DAT",
            Description = "Test DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = platformId,
            OriginalFilename = "test.dat",
            FileId = fileId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedLibraryAsync(RomdDbContext context, int libraryId)
    {
        context.Libraries.Add(new LibraryEntity
        {
            Id = libraryId,
            Name = $"Library {libraryId}",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedFileAndRomAsync(
        RomdDbContext context,
        int romId,
        int fileId,
        Sha1 sha1,
        Md5 md5,
        Crc32 crc32)
    {
        context.Files.Add(NewFile(fileId, DateTimeOffset.UtcNow));
        context.RomFiles.Add(new RomFileEntity
        {
            Id = romId,
            OriginalFilename = $"rom-{romId}.bin",
            FileId = fileId,
            Sha1 = sha1,
            Md5 = md5,
            Crc32 = crc32,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedFileAsync(RomdDbContext context, int fileId, DateTimeOffset createdAt)
    {
        context.Files.Add(NewFile(fileId, createdAt));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static FileEntityPersistence NewFile(int id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Sha256 = Sha256.Parse(string.Concat(Enumerable.Repeat($"{id:x2}", 32))),
        Size = 1,
        SizeOnDisk = 1,
        CreatedAt = createdAt
    };

    private static string[] StatsEventTypes() =>
    [
        AdminRealtimeEventTypes.StorageStatsChanged,
        AdminRealtimeEventTypes.CoverageStatsChanged,
        AdminRealtimeEventTypes.HealthStatsChanged
    ];

    private static Task<string[]> GetEventTypesAsync(RomdDbContext context) =>
        context.AdminRealtimeOutboxEvents
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .Select(row => row.EventType)
            .ToArrayAsync();

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            RomdDbContext context,
            InjectedCommitUnitOfWork unitOfWork)
        {
            _connection = connection;
            Context = context;
            UnitOfWork = unitOfWork;
            Outbox = new AdminRealtimeOutbox(context, TimeProvider.System);
        }

        public RomdDbContext Context { get; }
        public InjectedCommitUnitOfWork UnitOfWork { get; }
        public AdminRealtimeOutbox Outbox { get; }

        public RomdDbContext CreateReadContext() =>
            new(new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(_connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options);

        public static async Task<TestDatabase> CreateAsync(int? failCommitNumber = null)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            return new TestDatabase(
                connection,
                context,
                new InjectedCommitUnitOfWork(new EfUnitOfWork(context), context, failCommitNumber));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ThrowOnDisposeAfterCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new ThrowOnDisposeAfterCommitTransaction(await inner.BeginTransactionAsync(cancellationToken));

        private sealed class ThrowOnDisposeAfterCommitTransaction(ITransaction transaction) : ITransaction
        {
            private bool _committed;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await transaction.CommitAsync(cancellationToken);
                _committed = true;
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                transaction.RollbackAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (_committed)
                {
                    throw new InvalidOperationException("Injected post-commit disposal failure.");
                }
            }
        }
    }

    private sealed class CancelAfterCommitFlushUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new CancelAfterCommitFlushTransaction(
                inner,
                await inner.BeginTransactionAsync(cancellationToken),
                cancellation);

        private sealed class CancelAfterCommitFlushTransaction(
            IUnitOfWork unitOfWork,
            ITransaction transaction,
            CancellationTokenSource cancellation) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await unitOfWork.FlushAsync(cancellationToken);
                await cancellation.CancelAsync();
                await transaction.RollbackAsync(CancellationToken.None);
                throw new OperationCanceledException(cancellation.Token);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                transaction.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }

    private sealed class InjectedCommitUnitOfWork(
        IUnitOfWork inner,
        RomdDbContext context,
        int? failCommitNumber) : IUnitOfWork
    {
        private int _commitCount;
        private readonly int? _failCommitNumber = failCommitNumber;

        public int RollbackCleanupCount { get; private set; }

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new InjectedCommitTransaction(
                await inner.BeginTransactionAsync(cancellationToken),
                this);

        private void ObserveRollbackCleanup()
        {
            context.ChangeTracker.Entries().ShouldBeEmpty();
            RollbackCleanupCount++;
        }

        private sealed class InjectedCommitTransaction(
            ITransaction transaction,
            InjectedCommitUnitOfWork owner) : ITransaction
        {
            private bool _completed;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                int commitNumber = Interlocked.Increment(ref owner._commitCount);
                if (owner._failCommitNumber == commitNumber)
                {
                    // Flush staged writes into PostgreSQL before injecting the failure so the
                    // rollback assertions cannot be satisfied by an empty transaction.
                    await owner.FlushAsync(cancellationToken);
                    await transaction.RollbackAsync(CancellationToken.None);
                    owner.ObserveRollbackCleanup();
                    _completed = true;
                    throw new InvalidOperationException($"Injected failure for commit {commitNumber}.");
                }

                await transaction.CommitAsync(cancellationToken);
                _completed = true;
            }

            public async Task RollbackAsync(CancellationToken cancellationToken = default)
            {
                if (!_completed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _completed = true;
                }
            }

            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
