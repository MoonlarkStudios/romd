using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class FileRepositoryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public FileRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task GetStorageStatsAsync_AttributesBytesToOwnerCategories()
    {
        using (var db = CreateDb())
        {
            await SeedOneFilePerCategoryAsync(db);
        }

        var repository = new FileRepository(CreateDb());

        var stats = await repository.GetStorageStatsAsync(CancellationToken.None);

        // Global totals span every file regardless of owner.
        stats.TotalFiles.ShouldBe(5);
        stats.TotalSize.ShouldBe(1000 + 2000 + 300 + 4000 + 50);
        stats.TotalSizeOnDisk.ShouldBe(900 + 2000 + 300 + 1000 + 50);

        StorageCategoryStats Category(string key) => stats.Breakdown.Single(c => c.Category == key);

        var romMatched = Category(StorageCategories.RomMatched);
        romMatched.FileCount.ShouldBe(1);
        romMatched.Size.ShouldBe(1000);
        romMatched.SizeOnDisk.ShouldBe(900);

        var romUnmatched = Category(StorageCategories.RomUnmatched);
        romUnmatched.FileCount.ShouldBe(1);
        romUnmatched.Size.ShouldBe(2000); // content we hold but can't place (e.g. CHD) surfaces here

        Category(StorageCategories.Dat).Size.ShouldBe(300);
        Category(StorageCategories.Media).SizeOnDisk.ShouldBe(1000);

        var unattributed = Category(StorageCategories.Unattributed);
        unattributed.FileCount.ShouldBe(1);
        unattributed.Size.ShouldBe(50);
    }

    [Fact]
    public async Task GetUnreferencedFileIdsOlderThanAsync_ReturnsOnlyTheUnreferencedOrphan()
    {
        using (var db = CreateDb())
        {
            await SeedOneFilePerCategoryAsync(db);
        }

        var repository = new FileRepository(CreateDb());

        // Files 1-4 are referenced (matched ROM, unmatched ROM, DAT, media); only file 5 is
        // unreferenced. A future cutoff clears the age guard for every file.
        var orphans = await repository.GetUnreferencedFileIdsOlderThanAsync(
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        orphans.ShouldBe([5]);
    }

    [Fact]
    public async Task GetUnreferencedFileIdsOlderThanAsync_AgeGuard_SkipsRecentFiles()
    {
        using (var db = CreateDb())
        {
            await SeedOneFilePerCategoryAsync(db); // all files are stamped "now"
        }

        var repository = new FileRepository(CreateDb());

        // The orphan is unreferenced but recent, so a cutoff in the past must skip it — this is what
        // protects a file an in-flight upload has stored but not yet linked.
        var orphans = await repository.GetUnreferencedFileIdsOlderThanAsync(
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        orphans.ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    // Seeds exactly one stored file per attribution category:
    //   1 matched ROM, 1 unmatched ROM, 1 DAT source, 1 media, 1 unreferenced orphan.
    private static async Task SeedOneFilePerCategoryAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Platforms.Add(new PlatformEntity
        {
            Id = 1, Name = "Super Nintendo", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes", Manufacturer = "Nintendo",
            CreatedAt = now, CreatedByUserId = userId
        });

        db.Files.AddRange(
            File(1, 1000, 900), // matched ROM
            File(2, 2000, 2000), // unmatched ROM
            File(3, 300, 300), // DAT source
            File(4, 4000, 1000), // media
            File(5, 50, 50)); // unattributed orphan

        // DAT source file (category: dat) plus a DAT game to anchor the matched ROM.
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1, Name = "DAT", Description = "d", Type = "NoIntro", PlatformId = 1,
            OriginalFilename = "x.dat", FileId = 3, GameCount = 1, CreatedAt = now, CreatedByUserId = userId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1, CatalogSourceId = 1, EntryKey = "Game", Name = "Game", PlatformId = 1,
            CreatedAt = now, CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = 1, DatFileId = 1, SourceEntryId = 1, Name = "Game", CreatedAt = now, CreatedByUserId = userId
        });

        // Matched ROM: RomFile -> File 1, with a DatRom pointing at it.
        db.RomFiles.Add(RomFile(1, fileId: 1, hashByte: 1));
        db.DatRoms.Add(new DatRomEntity
        {
            Id = 1, DatGameId = 1, Name = "game.sfc", Size = 1000, Sha1 = NewSha1(1), RomFileId = 1,
            CreatedAt = now, CreatedByUserId = userId
        });

        // Unmatched ROM: RomFile -> File 2, no DatRom references it.
        db.RomFiles.Add(RomFile(2, fileId: 2, hashByte: 2));

        // Media: Title -> TitleMedia -> File 4.
        db.Titles.Add(new TitleEntity
        {
            Id = 1, PlatformId = 1, Name = "Title", NormalizedName = "title",
            EnrichmentStatus = "None",
            FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}", ScreenshotPrefsJson = "{}",
            CreatedAt = now, CreatedByUserId = userId
        });
        db.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 1, TitleId = 1, Type = "Cover", FileId = 4, SourceId = "user", IsPrimary = true,
            ContentType = "image/jpeg", CreatedAt = now, CreatedByUserId = userId
        });

        await db.SaveChangesAsync();

        FileEntityPersistence File(int id, long size, long sizeOnDisk) => new()
        {
            Id = id,
            Sha256 = NewSha256((byte)id),
            Size = size,
            SizeOnDisk = sizeOnDisk,
            IsCompressed = sizeOnDisk < size,
            CreatedAt = now,
            CreatedByUserId = userId
        };

        RomFileEntity RomFile(int id, int fileId, byte hashByte) => new()
        {
            Id = id,
            FileId = fileId,
            OriginalFilename = $"rom-{id}.bin",
            Sha1 = NewSha1(hashByte),
            Md5 = NewMd5(hashByte),
            Crc32 = Crc32.FromUInt32((uint)(0x01020300 + hashByte)),
            CreatedAt = now,
            CreatedByUserId = userId
        };
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
