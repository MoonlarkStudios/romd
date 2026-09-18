using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Domain.Storage;
using Romd.Persistence;
using Romd.Infrastructure.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

/// <summary>
///     Real-SQLite proofs that the #85 Slice B ROM/media mutations and their
///     primary event intents share one transaction, while file cleanup remains
///     post-commit work.
/// </summary>
public sealed class AdminMediaMutationOutboxAtomicityTests
{
    private const int PlatformId = 10;
    private const int TitleId = 20;
    private const int LibraryId = 30;
    private const int RomId = 40;
    private const int DatFileId = 50;
    private const int DatGameId = 60;
    private const int DatRomId = 70;
    private const int RomFileId = 80;
    private const int DatStorageFileId = 81;
    private const int ExistingMediaId = 90;
    private const int ExistingMediaFileId = 91;
    private const int ProviderMediaId = 92;
    private const int ProviderMediaFileId = 93;
    private const int UploadedMediaFileId = 94;

    [Fact]
    public async Task DeleteRom_Success_CommitsLinkDeletionLibraryFlagAndEventsBeforeCleanup()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedMatchedRomAsync(database.Context);
        var storage = new RecordingFileStorageService(() => database.UnitOfWork.CommitSucceeded);
        var handler = CreateDeleteRomHandler(database, storage);

        var result = await handler.HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeFalse();
        await using var assertContext = database.CreateAssertionContext();
        (await assertContext.RomFiles.AnyAsync(row => row.Id == RomId)).ShouldBeFalse();
        (await assertContext.DatRoms.SingleAsync(row => row.Id == DatRomId)).RomFileId.ShouldBeNull();
        (await assertContext.Libraries.SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeTrue();
        (await GetEventTypesAsync(assertContext)).ShouldBe(StatsEventTypes());
        storage.CleanupFileIds.ShouldBe([RomFileId]);
        storage.CleanupObservedCommitted.ShouldBe([true]);
    }

    [Fact]
    public async Task DeleteRom_CommitFails_RollsBackLinkDeletionLibraryFlagAndEventsWithoutCleanup()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedMatchedRomAsync(database.Context);
        var storage = new RecordingFileStorageService(() => database.UnitOfWork.CommitSucceeded);
        var handler = CreateDeleteRomHandler(database, storage);

        var result = await handler.HandleAsync(new DeleteRomCommand(RomId));

        result.IsError.ShouldBeTrue();
        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using var assertContext = database.CreateAssertionContext();
        (await assertContext.RomFiles.AnyAsync(row => row.Id == RomId)).ShouldBeTrue();
        (await assertContext.DatRoms.SingleAsync(row => row.Id == DatRomId)).RomFileId.ShouldBe(RomId);
        (await assertContext.Libraries.SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeFalse();
        (await GetEventTypesAsync(assertContext)).ShouldBeEmpty();
        storage.CleanupFileIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadTitleMedia_Success_RetainsPreviousImageAndSelectionWithStorageEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedTitleMediaAsync(database.Context);
        var uploadedFile = NewFile(UploadedMediaFileId).ToDomain();
        var storage = new RecordingFileStorageService(
            () => database.UnitOfWork.CommitSucceeded,
            new FileStoreResult(uploadedFile, WasNew: true, WasDeduplicated: false));
        var handler = CreateUploadTitleMediaHandler(database, storage);

        var result = await handler.HandleAsync(new UploadTitleMediaCommand(
            TitleId,
            MediaType.Cover,
            new MemoryStream(PngHeader),
            "replacement.png"));

        result.IsError.ShouldBeFalse();
        await using var assertContext = database.CreateAssertionContext();
        var media = await assertContext.TitleMedia
            .AsNoTracking()
            .Where(row => row.TitleId == TitleId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        media.Single(row => row.Id == ExistingMediaId).IsPrimary.ShouldBeTrue();
        var replacement = media.Single(row => row.FileId == UploadedMediaFileId);
        replacement.FileId.ShouldBe(UploadedMediaFileId);
        replacement.ContentType.ShouldBe("image/png");
        replacement.IsPrimary.ShouldBeFalse();
        result.Value.Id.ShouldNotBe("0");
        media.Single(row => row.Id == ProviderMediaId).IsPrimary.ShouldBeFalse();
        (await GetEventTypesAsync(assertContext)).ShouldBe([AdminRealtimeEventTypes.StorageStatsChanged]);
        storage.CleanupFileIds.ShouldBeEmpty();
        storage.CleanupObservedCommitted.ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadTitleMedia_CommitFails_RollsBackReplacementAndEventWithoutCleanup()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedTitleMediaAsync(database.Context);
        var uploadedFile = NewFile(UploadedMediaFileId).ToDomain();
        var storage = new RecordingFileStorageService(
            () => database.UnitOfWork.CommitSucceeded,
            new FileStoreResult(uploadedFile, WasNew: true, WasDeduplicated: false));
        var handler = CreateUploadTitleMediaHandler(database, storage);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(new UploadTitleMediaCommand(
            TitleId,
            MediaType.Cover,
            new MemoryStream(PngHeader),
            "replacement.png")));

        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using var assertContext = database.CreateAssertionContext();
        var media = await assertContext.TitleMedia
            .AsNoTracking()
            .Where(row => row.TitleId == TitleId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        media.Count.ShouldBe(2);
        var original = media.Single(row => row.Id == ExistingMediaId);
        original.FileId.ShouldBe(ExistingMediaFileId);
        original.IsPrimary.ShouldBeTrue();
        media.ShouldNotContain(row => row.FileId == UploadedMediaFileId);
        (await GetEventTypesAsync(assertContext)).ShouldBeEmpty();
        storage.CleanupFileIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteTitleMedia_Success_CommitsDeletionAndStorageEventBeforeCleanup()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedTitleMediaAsync(database.Context);
        var storage = new RecordingFileStorageService(() => database.UnitOfWork.CommitSucceeded);
        var handler = CreateDeleteTitleMediaHandler(database, storage);

        var result = await handler.HandleAsync(new DeleteTitleMediaCommand(TitleId, ExistingMediaId));

        result.IsError.ShouldBeFalse();
        await using var assertContext = database.CreateAssertionContext();
        var media = await assertContext.TitleMedia
            .AsNoTracking()
            .Where(row => row.TitleId == TitleId)
            .ToListAsync();
        media.ShouldHaveSingleItem();
        media[0].Id.ShouldBe(ProviderMediaId);
        media[0].IsPrimary.ShouldBeTrue();
        (await GetEventTypesAsync(assertContext)).ShouldBe([AdminRealtimeEventTypes.StorageStatsChanged]);
        storage.CleanupFileIds.ShouldBe([ExistingMediaFileId]);
        storage.CleanupObservedCommitted.ShouldBe([true]);
    }

    [Fact]
    public async Task DeleteTitleMedia_CommitFails_RollsBackDeletionAndEventWithoutCleanup()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedTitleMediaAsync(database.Context);
        var storage = new RecordingFileStorageService(() => database.UnitOfWork.CommitSucceeded);
        var handler = CreateDeleteTitleMediaHandler(database, storage);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new DeleteTitleMediaCommand(TitleId, ExistingMediaId)));

        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using var assertContext = database.CreateAssertionContext();
        var media = await assertContext.TitleMedia
            .AsNoTracking()
            .Where(row => row.TitleId == TitleId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        media.Count.ShouldBe(2);
        media.Single(row => row.Id == ExistingMediaId).IsPrimary.ShouldBeTrue();
        media.Single(row => row.Id == ProviderMediaId).IsPrimary.ShouldBeFalse();
        (await GetEventTypesAsync(assertContext)).ShouldBeEmpty();
        storage.CleanupFileIds.ShouldBeEmpty();
    }

    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];

    private static DeleteRomCommandHandler CreateDeleteRomHandler(
        TestDatabase database,
        IFileStorageService storage)
    {
        var impactReader = new RomCatalogOwnershipReader(database.Context);
        return new DeleteRomCommandHandler(
            new RomRepository(database.Context),
            impactReader,
            impactReader,
            storage,
            database.Outbox,
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            new TitlePayloadAvailabilityProjection(
                database.Context,
                new CatalogPayloadAssertionReader(database.Context,
                    [new DatCatalogPayloadAssertionProvider(database.Context)])),
            NullLogger<DeleteRomCommandHandler>.Instance);
    }

    private static UploadTitleMediaCommandHandler CreateUploadTitleMediaHandler(
        TestDatabase database,
        IFileStorageService storage) =>
        new(
            new TitleRepository(database.Context),
            storage,
            new MemoryTempFileFactory(),
            database.Outbox,
            database.UnitOfWork,
            NullLogger<UploadTitleMediaCommandHandler>.Instance);

    private static DeleteTitleMediaCommandHandler CreateDeleteTitleMediaHandler(
        TestDatabase database,
        IFileStorageService storage) =>
        new(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(database.Context),
            storage,
            database.Outbox,
            database.UnitOfWork,
            Options.Create(new EnrichmentOptions()),
            NullLogger<DeleteTitleMediaCommandHandler>.Instance);

    private static async Task SeedMatchedRomAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(NewPlatform(now));
        context.Files.AddRange(NewFile(RomFileId), NewFile(DatStorageFileId));
        context.Titles.Add(NewTitle(now));
        context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = DatFileId, Kind = "Dat", Status = "Active" }
            },
            Id = DatFileId,
            Name = "Atomicity DAT",
            Description = "Atomicity DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = PlatformId,
            OriginalFilename = "atomicity.dat",
            FileId = DatStorageFileId,
            CreatedAt = now
        });
        context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = DatGameId,
            CatalogSourceId = DatFileId,
            EntryKey = "Matched Game",
            Name = "Matched Game",
            PlatformId = PlatformId,
            CreatedAt = now
        });
        context.DatGames.Add(new DatGameEntity
        {
            Id = DatGameId,
            DatFileId = DatFileId,
            SourceEntryId = DatGameId,
            Name = "Matched Game",
            CreatedAt = now
        });
        context.RomFiles.Add(new RomFileEntity
        {
            Id = RomId,
            OriginalFilename = "matched.rom",
            FileId = RomFileId,
            Sha1 = Sha1.Parse(RomId.ToString("x40")),
            Md5 = Md5.Parse(RomId.ToString("x32")),
            Crc32 = Crc32.Parse(RomId.ToString("x8")),
            CreatedAt = now
        });
        context.DatRoms.Add(new DatRomEntity
        {
            Id = DatRomId,
            DatGameId = DatGameId,
            Name = "matched.rom",
            Size = 12,
            Sha1 = Sha1.Parse(RomId.ToString("x40")),
            RomFileId = RomId,
            CreatedAt = now
        });
        context.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = DatGameId,
            TitleId = TitleId,
            CreatedAt = now
        });
        var library = LibraryEntity.FromDomain(Library.CreateNew(
            "Matched platform library",
            new LibraryConfiguration { AllowedPlatformIds = [PlatformId] }));
        library.Id = LibraryId;
        library.NeedsMaterialization = false;
        context.Libraries.Add(library);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedTitleMediaAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(NewPlatform(now));
        context.Files.AddRange(
            NewFile(ExistingMediaFileId),
            NewFile(ProviderMediaFileId),
            NewFile(UploadedMediaFileId));
        context.Titles.Add(NewTitle(now));
        context.TitleMedia.AddRange(
            new TitleMediaEntity
            {
                Id = ExistingMediaId,
                TitleId = TitleId,
                Type = MediaType.Cover.ToString(),
                FileId = ExistingMediaFileId,
                SourceId = "user",
                IsPrimary = true,
                ContentType = "image/jpeg",
                CreatedAt = now.AddMinutes(-2)
            },
            new TitleMediaEntity
            {
                Id = ProviderMediaId,
                TitleId = TitleId,
                Type = MediaType.Cover.ToString(),
                FileId = ProviderMediaFileId,
                SourceId = "igdb",
                IsPrimary = false,
                ContentType = "image/jpeg",
                CreatedAt = now.AddMinutes(-1)
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static PlatformEntity NewPlatform(DateTimeOffset now) => new()
    {
        Id = PlatformId,
        Name = "Atomicity Platform",
        Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Atomicity Platform", BaseCompactLabel = "Atomicity Platform", CanonicalKey = "atomicity", ShortName = "atomicity",
        CreatedAt = now
    };

    private static TitleEntity NewTitle(DateTimeOffset now) => new()
    {
        Id = TitleId,
        PlatformId = PlatformId,
        Name = "Atomicity Title",
        NormalizedName = "atomicity title",
        EnrichmentStatus = EnrichmentStatus.None.ToString(),
        FieldProvenanceJson = "{}",
        FieldSourceOverridesJson = "{}",
        ScreenshotPrefsJson = "{}",
        CreatedAt = now
    };

    private static FileEntityPersistence NewFile(int id) => new()
    {
        Id = id,
        Sha256 = Sha256.Parse(id.ToString("x64")),
        Size = 12,
        SizeOnDisk = 12,
        CreatedAt = DateTimeOffset.UtcNow
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
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            DbContextOptions<RomdDbContext> options,
            RomdDbContext context,
            CommitObservingUnitOfWork unitOfWork)
        {
            _connection = connection;
            _options = options;
            Context = context;
            UnitOfWork = unitOfWork;
            Outbox = new AdminRealtimeOutbox(context, TimeProvider.System);
        }

        public RomdDbContext Context { get; }
        public CommitObservingUnitOfWork UnitOfWork { get; }
        public AdminRealtimeOutbox Outbox { get; }

        public static async Task<TestDatabase> CreateAsync(bool failCommit = false)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            var context = new RomdDbContext(options);
            return new TestDatabase(
                connection,
                options,
                context,
                new CommitObservingUnitOfWork(new EfUnitOfWork(context), context, failCommit));
        }

        public RomdDbContext CreateAssertionContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CommitObservingUnitOfWork(
        IUnitOfWork inner,
        RomdDbContext context,
        bool failCommit) : IUnitOfWork
    {
        public bool CommitSucceeded { get; private set; }
        public int RollbackCleanupCount { get; private set; }

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new CommitObservingTransaction(
                await inner.BeginTransactionAsync(cancellationToken),
                this,
                failCommit);

        private void ObserveRollbackCleanup()
        {
            context.ChangeTracker.Entries().ShouldBeEmpty();
            RollbackCleanupCount++;
        }

        private sealed class CommitObservingTransaction(
            ITransaction inner,
            CommitObservingUnitOfWork owner,
            bool failCommit) : ITransaction
        {
            private bool _completed;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (failCommit)
                {
                    await inner.RollbackAsync(CancellationToken.None);
                    owner.ObserveRollbackCleanup();
                    _completed = true;
                    throw new InvalidOperationException("Injected commit failure after rollback.");
                }

                await inner.CommitAsync(cancellationToken);
                owner.CommitSucceeded = true;
                _completed = true;
            }

            public async Task RollbackAsync(CancellationToken cancellationToken = default)
            {
                if (!_completed)
                {
                    await inner.RollbackAsync(cancellationToken);
                    _completed = true;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class RecordingFileStorageService(
        Func<bool> commitSucceeded,
        FileStoreResult? storeResult = null) : IFileStorageService
    {
        public List<int> CleanupFileIds { get; } = [];
        public List<bool> CleanupObservedCommitted { get; } = [];

        public Task<FileStoreResult> StoreAsync(
            Stream content,
            IProgress<StoreProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromException<FileStoreResult>(new NotSupportedException());

        public Task<FileStoreResult> StoreFromTempFileAsync(ITempFile tempFile, CancellationToken ct = default) =>
            Task.FromResult(storeResult ?? throw new NotSupportedException());

        public Task<Stream?> RetrieveByIdAsync(int fileId, CancellationToken ct) =>
            Task.FromException<Stream?>(new NotSupportedException());

        public Task<Stream?> RetrieveBySha256Async(Sha256 sha256, CancellationToken ct) =>
            Task.FromException<Stream?>(new NotSupportedException());

        public Task<bool> ExistsAsync(int fileId, CancellationToken ct) =>
            Task.FromException<bool>(new NotSupportedException());

        public Task<bool> DeleteIfUnreferencedAsync(int fileId, CancellationToken ct)
        {
            CleanupFileIds.Add(fileId);
            CleanupObservedCommitted.Add(commitSucceeded());
            return Task.FromResult(true);
        }

        public Task<int> PruneOrphanedFilesAsync(CancellationToken ct) =>
            Task.FromException<int>(new NotSupportedException());

        public Task<int> PruneUnreferencedFilesAsync(TimeSpan minimumAge, CancellationToken ct) =>
            Task.FromException<int>(new NotSupportedException());
    }

    private sealed class MemoryTempFileFactory : ITempFileFactory
    {
        public async Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return new MemoryTempFile(buffer.ToArray());
        }
    }

    private sealed class MemoryTempFile(byte[] content) : ITempFile
    {
        public string Path => "memory.png";
        public Sha256 FileSha256 { get; } = Sha256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        public long FileSize => content.Length;
        public Stream OpenRead() => new MemoryStream(content);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
