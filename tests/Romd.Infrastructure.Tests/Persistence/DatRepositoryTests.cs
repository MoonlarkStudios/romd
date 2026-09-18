using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class DatRepositoryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public DatRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task LinkDatRomsToRomFileAsync_UnlinkedDatRom_UpdatesRomFileId()
    {
        using var db = CreateDb();
        await SeedRomLinkDataAsync(db);
        var repository = new DatRepository(db, TimeProvider.System);

        int linkedCount = await repository.LinkDatRomsToRomFileAsync(NewSha1(1), 1);

        linkedCount.ShouldBe(1);
        int? romFileId = await db.DatRoms
            .Where(rom => rom.Id == 1)
            .Select(rom => rom.RomFileId)
            .SingleAsync();
        romFileId.ShouldBe(1);
    }

    [Fact]
    public async Task LinkDatRomsToExistingRomFilesAsync_MatchingExistingRom_UpdatesRomFileId()
    {
        using var db = CreateDb();
        await SeedRomLinkDataAsync(db);
        var repository = new DatRepository(db, TimeProvider.System);

        int linkedCount = await repository.LinkDatRomsToExistingRomFilesAsync([1]);

        linkedCount.ShouldBe(1);
        int? romFileId = await db.DatRoms
            .Where(rom => rom.Id == 1)
            .Select(rom => rom.RomFileId)
            .SingleAsync();
        romFileId.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireMutationWriteLockAsync_WithoutCallerTransaction_Throws()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        var repository = new DatRepository(db, TimeProvider.System);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            repository.AcquireMutationWriteLockAsync(7));

        exception.Message.ShouldContain("caller-owned transaction");
    }

    [Fact]
    public async Task AcquireMutationWriteLockAsync_InsideCallerTransaction_PreservesDatState()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        var repository = new DatRepository(db, TimeProvider.System);
        DateTimeOffset? updatedAt = await db.DatFiles
            .Where(dat => dat.Id == 7)
            .Select(dat => dat.UpdatedAt)
            .SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await repository.AcquireMutationWriteLockAsync(7);

        (await db.DatFiles.Where(dat => dat.Id == 7).Select(dat => dat.UpdatedAt).SingleAsync())
            .ShouldBe(updatedAt);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AcquireMutationWriteLockAsync_SerializesConcurrentTopologyWriter()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = new RomdDbContext(database.CreateOptions());
        await SeedWriterFenceGraphAsync(first);
        await using var transaction = await first.Database.BeginTransactionAsync();

        await new DatRepository(first, TimeProvider.System)
            .AcquireMutationWriteLockAsync(7);

        var fenceRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWriter = Task.Run(async () =>
        {
            await using var second = new RomdDbContext(database.CreateOptions(options =>
                options.AddInterceptors(new CommandStartedInterceptor(fenceRequested))));
            await using var secondTransaction = await second.Database.BeginTransactionAsync();
            await new DatRepository(second, TimeProvider.System)
                .AcquireMutationWriteLockAsync(7);
            await second.TitleSourceLinks
                .Where(link => link.SourceEntryId == 30)
                .ExecuteUpdateAsync(update => update.SetProperty(link => link.TitleId, 202));
            await secondTransaction.CommitAsync();
        });

        await fenceRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await Task.WhenAny(secondWriter, Task.Delay(200))).ShouldNotBe(secondWriter);

        await transaction.CommitAsync();
        await secondWriter.WaitAsync(TimeSpan.FromSeconds(5));
        first.ChangeTracker.Clear();
        (await first.TitleSourceLinks.SingleAsync(link => link.SourceEntryId == 30))
            .TitleId.ShouldBe(202);
    }

    /// <summary>
    ///     No-Intro headers carry the whole contributor list as the author (486 characters in the
    ///     N64 fixture). SQLite ignored the old 200-character limit; PostgreSQL enforces it, so the
    ///     column is unbounded text.
    /// </summary>
    [Fact]
    public async Task DatFileAuthor_ContributorListLongerThanLegacyLimit_RoundTrips()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 4096, sizeOnDisk: 1024);
        string contributors = string.Join(", ", Enumerable.Range(1, 120).Select(index => $"contributor-{index}"));
        contributors.Length.ShouldBeGreaterThan(486);

        db.Model.FindEntityType(typeof(DatFileEntity))!.FindProperty(nameof(DatFileEntity.Author))!
            .GetMaxLength().ShouldBeNull();
        await db.DatFiles
            .Where(dat => dat.Id == 7)
            .ExecuteUpdateAsync(setters => setters.SetProperty(dat => dat.Author, contributors));

        string? persisted = await db.DatFiles.Where(dat => dat.Id == 7).Select(dat => dat.Author).SingleAsync();
        persisted.ShouldBe(contributors);
    }

    [Fact]
    public async Task GetByIdWithSizeAsync_JoinsStoredSourceFileSize()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 4096, sizeOnDisk: 1024);
        var repository = new DatRepository(db, TimeProvider.System);

        var result = await repository.GetByIdWithSizeAsync(7);

        result.ShouldNotBeNull();
        result.Dat.Id.ShouldBe(7);
        result.SizeBytes.ShouldBe(4096);
        result.SizeOnDiskBytes.ShouldBe(1024);
        result.IsCompressed.ShouldBe(true); // sizeOnDisk < size
    }

    [Fact]
    public async Task GetAllWithSizeAsync_ReturnsEachDatWithItsStoredFileSize()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 4096, sizeOnDisk: 1024);
        await SeedDatWithFileAsync(db, datId: 8, fileId: 6, size: 100, sizeOnDisk: 100);
        var repository = new DatRepository(db, TimeProvider.System);

        var results = await repository.GetAllWithSizeAsync();

        results.Count.ShouldBe(2);
        results.Single(r => r.Dat.Id == 7).SizeOnDiskBytes.ShouldBe(1024);
        results.Single(r => r.Dat.Id == 8).SizeBytes.ShouldBe(100);
    }

    [Fact]
    public async Task GetByIdWithSizeAsync_CarriesTheSourcesCatalogStatusAndId()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(
            db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10,
            sourceStatus: nameof(CatalogSourceStatus.Disabled), catalogSourceId: 107);
        var repository = new DatRepository(db, TimeProvider.System);

        var result = await repository.GetByIdWithSizeAsync(7);

        result.ShouldNotBeNull();
        result.SourceStatus.ShouldBe(CatalogSourceStatus.Disabled);
        result.CatalogSourceId.ShouldBe(107);
    }

    [Fact]
    public async Task GetAllWithSizeAsync_CarriesEachSourcesCatalogStatusAndId()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10, catalogSourceId: 107);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10,
            sourceStatus: nameof(CatalogSourceStatus.Disabled), catalogSourceId: 108);
        var repository = new DatRepository(db, TimeProvider.System);

        var results = await repository.GetAllWithSizeAsync();

        results.Single(r => r.Dat.Id == 7).SourceStatus.ShouldBe(CatalogSourceStatus.Active);
        results.Single(r => r.Dat.Id == 7).CatalogSourceId.ShouldBe(107);
        results.Single(r => r.Dat.Id == 8).SourceStatus.ShouldBe(CatalogSourceStatus.Disabled);
        results.Single(r => r.Dat.Id == 8).CatalogSourceId.ShouldBe(108);
    }

    [Fact]
    public async Task GetUnroutedAsync_CarriesEachSourcesCatalogStatusAndId()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10, catalogSourceId: 107);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10,
            sourceStatus: nameof(CatalogSourceStatus.Disabled), catalogSourceId: 108);
        var repository = new DatRepository(db, TimeProvider.System);

        var results = await repository.GetUnroutedAsync();

        results.Count.ShouldBe(2);
        results.Single(r => r.Dat.Id == 7).SourceStatus.ShouldBe(CatalogSourceStatus.Active);
        results.Single(r => r.Dat.Id == 7).CatalogSourceId.ShouldBe(107);
        results.Single(r => r.Dat.Id == 8).SourceStatus.ShouldBe(CatalogSourceStatus.Disabled);
        results.Single(r => r.Dat.Id == 8).CatalogSourceId.ShouldBe(108);
    }

    [Fact]
    public async Task GetByPlatformIdAsync_CarriesEachSourcesCatalogStatusAndId()
    {
        using var db = CreateDb();
        await SeedPlatformAsync(db, platformId: 3);
        await SeedDatWithFileAsync(
            db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10, platformId: 3, catalogSourceId: 107);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10, platformId: 3,
            sourceStatus: nameof(CatalogSourceStatus.Disabled), catalogSourceId: 108);
        var repository = new DatRepository(db, TimeProvider.System);

        var results = await repository.GetByPlatformIdAsync(3);

        results.Count.ShouldBe(2);
        results.Single(r => r.Dat.Id == 7).SourceStatus.ShouldBe(CatalogSourceStatus.Active);
        results.Single(r => r.Dat.Id == 7).CatalogSourceId.ShouldBe(107);
        results.Single(r => r.Dat.Id == 8).SourceStatus.ShouldBe(CatalogSourceStatus.Disabled);
        results.Single(r => r.Dat.Id == 8).CatalogSourceId.ShouldBe(108);
    }

    [Fact]
    public async Task GetUnroutedAsync_SourceRowsDeletedConcurrently_ReturnsOnlyConsistentRows()
    {
        // The enriched read joins DAT, source anchor, and catalog source in one query, so a
        // DAT whose source rows vanished mid-flight is omitted instead of surfacing a torn
        // pairing (the old two-read composition threw KeyNotFoundException here).
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        await SeedDatWithFileAsync(db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10);
        await db.Database.ExecuteSqlRawAsync(
            """
            SET session_replication_role = replica;
            DELETE FROM "DatSources" WHERE "Id" = 8;
            DELETE FROM "CatalogSources" WHERE "Id" = 8;
            SET session_replication_role = DEFAULT;
            """);
        var repository = new DatRepository(db, TimeProvider.System);

        var results = await repository.GetUnroutedAsync();

        results.Select(r => r.Dat.Id).ShouldBe([7]);
    }

    [Fact]
    public async Task Listings_SupersededVersion_ExcludedWhileGetByIdRemainsUnfiltered()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10,
            lifecycle: nameof(DatFileLifecycle.Superseded));
        var repository = new DatRepository(db, TimeProvider.System);

        (await repository.GetAllAsync()).Select(d => d.Id).ShouldBe([7]);
        (await repository.GetAllWithSizeAsync()).Select(r => r.Dat.Id).ShouldBe([7]);
        (await repository.GetUnroutedAsync()).Select(r => r.Dat.Id).ShouldBe([7]);

        var superseded = await repository.GetByIdAsync(8);
        superseded.ShouldNotBeNull();
        superseded.Lifecycle.ShouldBe(DatFileLifecycle.Superseded);
        (await repository.GetByIdWithSizeAsync(8)).ShouldNotBeNull();
    }

    [Fact]
    public async Task GetByPlatformIdAsync_SupersededVersion_Excluded()
    {
        using var db = CreateDb();
        await SeedPlatformAsync(db, platformId: 3);
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10, platformId: 3);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10, platformId: 3,
            lifecycle: nameof(DatFileLifecycle.Superseded));
        var repository = new DatRepository(db, TimeProvider.System);

        (await repository.GetByPlatformIdAsync(3)).Select(r => r.Dat.Id).ShouldBe([7]);
    }

    [Fact]
    public async Task SupersededVersionRemoval_PrunesEntriesOnlyItReferencedWithTheirLinks()
    {
        using var db = CreateDb();
        await SeedPlatformWithTitlesAsync(db, titleIds: [1, 2]);
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10,
            lifecycle: nameof(DatFileLifecycle.Superseded), datSourceId: 7);
        var repository = new DatRepository(db, TimeProvider.System);
        int keptEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Kept");
        int removedEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Removed");
        var activeGameIds = await repository.AddGamesBatchAsync(
            [new DatGameWithEntry(DatGame.CreateNew(7, "Kept"), keptEntryId)]);
        var supersededGameIds = await repository.AddGamesBatchAsync([
            new DatGameWithEntry(DatGame.CreateNew(8, "Kept"), keptEntryId),
            new DatGameWithEntry(DatGame.CreateNew(8, "Removed"), removedEntryId)
        ]);
        await repository.UpsertAssignmentsAsync([
            new TitleSourceAssignment(activeGameIds[0], 1),
            new TitleSourceAssignment(supersededGameIds[1], 2)
        ]);

        // Retention keeps the single superseded version, so nothing is pruned (idempotent).
        (await repository.DeleteSupersededVersionsBeyondMostRecentAsync(7)).ShouldBe(0);
        (await repository.DeleteSupersededVersionsBeyondMostRecentAsync(7)).ShouldBe(0);
        (await db.SourceEntries.CountAsync()).ShouldBe(2);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(2);

        // Deleting the retained superseded version prunes the entry only it referenced,
        // cascading its link; the shared entry keeps its link (Active still asserts it).
        await repository.DeleteAsync(8);

        var keptEntry = (await db.SourceEntries.ToListAsync()).ShouldHaveSingleItem();
        keptEntry.EntryKey.ShouldBe("Kept");
        var keptLink = (await db.TitleSourceLinks.ToListAsync()).ShouldHaveSingleItem();
        keptLink.SourceEntryId.ShouldBe(keptEntry.Id);
        keptLink.TitleId.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAbandonedPendingVersionsAsync_PrunesPendingOnlyEntryAndLink()
    {
        using var db = CreateDb();
        await SeedPlatformWithTitlesAsync(db, titleIds: [1]);
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        await SeedDatWithFileAsync(
            db, datId: 8, fileId: 6, size: 10, sizeOnDisk: 10,
            lifecycle: nameof(DatFileLifecycle.PendingActivation), datSourceId: 7);
        var repository = new DatRepository(db, TimeProvider.System);
        int activeEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Active Game");
        int pendingEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Pending Only");
        await repository.AddGamesBatchAsync(
            [new DatGameWithEntry(DatGame.CreateNew(7, "Active Game"), activeEntryId)]);
        var pendingGameIds = await repository.AddGamesBatchAsync(
            [new DatGameWithEntry(DatGame.CreateNew(8, "Pending Only"), pendingEntryId)]);
        await repository.UpsertAssignmentsAsync([new TitleSourceAssignment(pendingGameIds[0], 1)]);

        int deleted = await repository.DeleteAbandonedPendingVersionsAsync(
            DateTimeOffset.UtcNow.AddDays(1));

        deleted.ShouldBe(1);
        (await db.SourceEntries.Select(e => e.EntryKey).ToListAsync()).ShouldBe(["Active Game"]);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteSourceIfOrphanedAsync_NoVersionsRemain_CascadesCatalogSourceEntriesAndLinks()
    {
        using var db = CreateDb();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Linked Title",
            NormalizedName = "linked title",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        var repository = new DatRepository(db, TimeProvider.System);
        int linkedEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Linked Game");
        var gameIds = await repository.AddGamesBatchAsync(
            [new DatGameWithEntry(DatGame.CreateNew(7, "Linked Game"), linkedEntryId)]);
        await repository.UpsertAssignmentsAsync([new TitleSourceAssignment(gameIds[0], 1)]);

        // Deleting the version cascades its games at the database, orphaning the source.
        await repository.DeleteAsync(7);
        await repository.DeleteSourceIfOrphanedAsync(7);

        (await db.DatSources.CountAsync()).ShouldBe(0);
        (await db.CatalogSources.CountAsync()).ShouldBe(0);
        (await db.SourceEntries.CountAsync()).ShouldBe(0);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
        (await db.Titles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteSourceIfOrphanedAsync_VersionStillExists_IsNoOp()
    {
        using var db = CreateDb();
        await SeedDatWithFileAsync(db, datId: 7, fileId: 5, size: 10, sizeOnDisk: 10);
        var repository = new DatRepository(db, TimeProvider.System);
        int keptEntryId = await SeedSourceEntryAsync(db, catalogSourceId: 7, entryKey: "Kept Game");
        await repository.AddGamesBatchAsync(
            [new DatGameWithEntry(DatGame.CreateNew(7, "Kept Game"), keptEntryId)]);

        await repository.DeleteSourceIfOrphanedAsync(7);

        (await db.DatSources.CountAsync()).ShouldBe(1);
        (await db.CatalogSources.CountAsync()).ShouldBe(1);
        (await db.SourceEntries.CountAsync()).ShouldBe(1);
    }

    public void Dispose() => _connection.Dispose();

    private static async Task SeedPlatformAsync(RomdDbContext db, int platformId)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedDatWithFileAsync(
        RomdDbContext db,
        int datId,
        int fileId,
        long size,
        long sizeOnDisk,
        int? platformId = null,
        string lifecycle = nameof(DatFileLifecycle.Active),
        int? datSourceId = null,
        string sourceStatus = nameof(CatalogSourceStatus.Active),
        int? catalogSourceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Files.Add(new FileEntityPersistence
        {
            Id = fileId,
            Sha256 = NewSha256((byte)fileId),
            Size = size,
            SizeOnDisk = sizeOnDisk,
            IsCompressed = sizeOnDisk < size,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        var datFile = new DatFileEntity
        {
            Id = datId,
            Name = $"DAT {datId}",
            Description = "test dat",
            Type = "NoIntro",
            PlatformId = platformId,
            Lifecycle = lifecycle,
            OriginalFilename = $"dat-{datId}.dat",
            FileId = fileId,
            GameCount = 0,
            CreatedAt = now,
            CreatedByUserId = userId
        };
        if (datSourceId is int sourceId)
        {
            datFile.DatSourceId = sourceId;
        }
        else
        {
            datFile.Source = new DatSourceEntity
            {
                Id = datId,
                CatalogSource = new CatalogSourceEntity
                {
                    Id = catalogSourceId ?? datId,
                    Kind = "Dat",
                    Status = sourceStatus
                }
            };
        }

        db.DatFiles.Add(datFile);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    /// <summary>
    ///     Seeds the catalog-owned source entry a game realizes: entry identity comes from the
    ///     title derivation service in production, so repository tests seed it directly.
    /// </summary>
    private static async Task<int> SeedSourceEntryAsync(RomdDbContext db, int catalogSourceId, string entryKey)
    {
        var entry = new SourceEntryEntity
        {
            CatalogSourceId = catalogSourceId,
            EntryKey = entryKey,
            Name = entryKey,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SourceEntries.Add(entry);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return entry.Id;
    }

    private static async Task SeedPlatformWithTitlesAsync(RomdDbContext db, IReadOnlyList<int> titleIds)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Titles.AddRange(titleIds.Select(id => new TitleEntity
        {
            Id = id,
            PlatformId = 1,
            Name = $"Title {id}",
            NormalizedName = $"title {id}",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedRomLinkDataAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo Entertainment System", BaseCompactLabel = "Nintendo Entertainment System", CanonicalKey = "nes", ShortName = "nes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = NewSha256(1),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.RomFiles.Add(new RomFileEntity
        {
            Id = 1,
            OriginalFilename = "matched.nes",
            FileId = 1,
            Sha1 = NewSha1(1),
            Md5 = NewMd5(1),
            Crc32 = Crc32.FromUInt32(0x01020304),
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "No-Intro NES",
            Description = "NES DAT",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "nes.dat",
            FileId = 1,
            GameCount = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1,
            CatalogSourceId = 1,
            EntryKey = "Matched Game",
            Name = "Matched Game",
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatGames.Add(new DatGameEntity
        {
            Id = 1,
            DatFileId = 1,
            SourceEntryId = 1,
            Name = "Matched Game",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatRoms.Add(new DatRomEntity
        {
            Id = 1,
            DatGameId = 1,
            Name = "matched.nes",
            Size = 1,
            Sha1 = NewSha1(1),
            RomFileId = null,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedWriterFenceGraphAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Writer fence",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Writer fence", BaseCompactLabel = "Writer fence", CanonicalKey = "writer-fence", ShortName = "writer-fence",
            CreatedAt = now
        });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 17,
            Sha256 = NewSha256(17),
            Size = 1,
            SizeOnDisk = 1,
            CreatedAt = now
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = 7,
            Source = new DatSourceEntity
            {
                Id = 21,
                CatalogSource = new CatalogSourceEntity
                {
                    Id = 20,
                    Kind = nameof(CatalogSourceKind.Dat),
                    Status = nameof(CatalogSourceStatus.Active),
                    CreatedAt = now
                },
                CreatedAt = now
            },
            Name = "Writer fence DAT",
            Description = "Writer fence DAT",
            Type = nameof(DatType.NoIntro),
            PlatformId = 10,
            OriginalFilename = "writer-fence.dat",
            FileId = 17,
            CreatedAt = now
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 30,
            CatalogSourceId = 20,
            EntryKey = "writer-fence",
            Name = "Writer fence",
            PlatformId = 10,
            CreatedAt = now
        });
        db.Titles.AddRange(
            new TitleEntity
            {
                Id = 101,
                PlatformId = 10,
                Name = "Source",
                NormalizedName = "source",
                EnrichmentStatus = "None",
                CreatedAt = now
            },
            new TitleEntity
            {
                Id = 202,
                PlatformId = 10,
                Name = "Target",
                NormalizedName = "target",
                EnrichmentStatus = "None",
                CreatedAt = now
            });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 30,
            TitleId = 101,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class CommandStartedInterceptor(TaskCompletionSource started) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static Md5 NewMd5(byte firstByte)
    {
        var bytes = new byte[Md5.ByteLength];
        bytes[0] = firstByte;
        return Md5.FromSpan(bytes);
    }
}
