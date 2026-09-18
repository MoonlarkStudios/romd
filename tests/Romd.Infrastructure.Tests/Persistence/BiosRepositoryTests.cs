using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class BiosRepositoryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public BiosRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();

        using var seed = CreateDb();
        seed.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Sony PlayStation",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Sony PlayStation", BaseCompactLabel = "Sony PlayStation", CanonicalKey = "psx", ShortName = "psx",
            CreatedAt = DateTimeOffset.UtcNow
        });
        seed.SaveChanges();
    }

    [Fact]
    public async Task AddRangeAsync_AssignsIdsAndPersists()
    {
        var repository = new BiosRepository(CreateDb());

        var added = await repository.AddRangeAsync(
        [
            Bios.CreateNew(1, "[BIOS] PlayStation (USA)", "playstationusa"),
            Bios.CreateNew(1, "[BIOS] PlayStation (Japan)", "playstationjapan")
        ]);

        added.Count.ShouldBe(2);
        added.ShouldAllBe(b => b.Id > 0);

        using var verify = CreateDb();
        (await verify.Bios.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task GetByNormalizedNamesAsync_ReturnsOnlyMatchesForPlatform()
    {
        using (var db = CreateDb())
        {
            db.Bios.AddRange(
                new BiosEntity { Id = 1, PlatformId = 1, Name = "USA", NormalizedName = "playstationusa", CreatedAt = DateTimeOffset.UtcNow },
                new BiosEntity { Id = 2, PlatformId = 1, Name = "JP", NormalizedName = "playstationjapan", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var repository = new BiosRepository(CreateDb());

        var result = await repository.GetByNormalizedNamesAsync(1, ["playstationusa", "playstationeurope"]);

        result.Count.ShouldBe(1);
        result.ShouldContainKey("playstationusa");
        result.ShouldNotContainKey("playstationeurope");
    }

    [Fact]
    public async Task GetByPlatformWithOwnershipAsync_ReportsOwnedMissingAndDedupesAcrossRevisions()
    {
        using (var db = CreateDb())
        {
            SeedOwnershipGraph(db);
            await db.SaveChangesAsync();
        }

        var repository = new BiosRepository(CreateDb());

        var result = await repository.GetByPlatformWithOwnershipAsync(1);

        result.Count.ShouldBe(2);

        var owned = result.Single(b => b.BiosId == 1);
        owned.TotalRoms.ShouldBe(1); // same SHA-1 listed in two DAT revisions counts once
        owned.OwnedRoms.ShouldBe(1);
        owned.IsOwned.ShouldBeTrue();
        owned.RequiredBytes.ShouldBe(512); // DAT-declared size of the one distinct ROM
        owned.OwnedBytes.ShouldBe(512);
        owned.OnDiskBytes.ShouldBe(256); // actual CAS on-disk size (compressed), not the DAT size

        var missing = result.Single(b => b.BiosId == 2);
        missing.TotalRoms.ShouldBe(1);
        missing.OwnedRoms.ShouldBe(0);
        missing.IsOwned.ShouldBeFalse();
        missing.RequiredBytes.ShouldBe(512); // required even though unowned
        missing.OwnedBytes.ShouldBe(0);
        missing.OnDiskBytes.ShouldBe(0);
    }

    [Fact]
    public async Task GetByPlatformWithOwnershipAsync_NoBios_ReturnsEmpty()
    {
        var repository = new BiosRepository(CreateDb());

        var result = await repository.GetByPlatformWithOwnershipAsync(1);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetUnmappedBiosGamesAsync_ReturnsOnlyUnmappedBiosOfPlatformAssignedDats()
    {
        using (var db = CreateDb())
        {
            var now = DateTimeOffset.UtcNow;

            db.Files.Add(new FileEntityPersistence
            {
                Id = 1,
                Sha256 = Sha256.Parse("0f" + new string('0', 62)),
                Size = 1,
                SizeOnDisk = 1,
                CreatedAt = now
            });

            // DAT assigned to platform 1.
            db.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
                },
                Id = 1, Name = "Assigned", Description = "d", Type = "Unknown", PlatformId = 1,
                OriginalFilename = "a.dat", FileId = 1, GameCount = 3, RomCount = 0, CreatedAt = now
            });
            // DAT not yet assigned to any platform.
            db.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Id = 2, Kind = "Dat", Status = "Active" }
                },
                Id = 2, Name = "Unrouted", Description = "d", Type = "Unknown", PlatformId = null,
                OriginalFilename = "b.dat", FileId = 1, GameCount = 1, RomCount = 0, CreatedAt = now
            });

            db.SourceEntries.AddRange(
                new SourceEntryEntity { Id = 10, CatalogSourceId = 1, EntryKey = "[BIOS] Unmapped", Name = "[BIOS] Unmapped", PlatformId = 1, CreatedAt = now },
                new SourceEntryEntity { Id = 11, CatalogSourceId = 1, EntryKey = "[BIOS] Mapped", Name = "[BIOS] Mapped", PlatformId = 1, CreatedAt = now },
                new SourceEntryEntity { Id = 12, CatalogSourceId = 1, EntryKey = "Regular Game", Name = "Regular Game", PlatformId = 1, CreatedAt = now },
                new SourceEntryEntity { Id = 20, CatalogSourceId = 2, EntryKey = "[BIOS] Unrouted", Name = "[BIOS] Unrouted", CreatedAt = now });
            db.DatGames.AddRange(
                new DatGameEntity { Id = 10, DatFileId = 1, SourceEntryId = 10, Name = "[BIOS] Unmapped", IsBios = true, CreatedAt = now },
                new DatGameEntity { Id = 11, DatFileId = 1, SourceEntryId = 11, Name = "[BIOS] Mapped", IsBios = true, CreatedAt = now },
                new DatGameEntity { Id = 12, DatFileId = 1, SourceEntryId = 12, Name = "Regular Game", IsBios = false, CreatedAt = now },
                new DatGameEntity { Id = 20, DatFileId = 2, SourceEntryId = 20, Name = "[BIOS] Unrouted", IsBios = true, CreatedAt = now });

            db.Bios.Add(new BiosEntity { Id = 1, PlatformId = 1, Name = "[BIOS] Mapped", NormalizedName = "mapped", CreatedAt = now });
            db.BiosGameMappings.Add(new BiosGameMappingEntity { DatGameId = 11, BiosId = 1, CreatedAt = now });

            await db.SaveChangesAsync();
        }

        var repository = new BiosRepository(CreateDb());

        var result = await repository.GetUnmappedBiosGamesAsync();

        result.Count.ShouldBe(1);
        result[0].GameId.ShouldBe(10);
        result[0].PlatformId.ShouldBe(1);
        result[0].Name.ShouldBe("[BIOS] Unmapped");
    }

    // Two BIOS entries on platform 1:
    //   Bios 1 ("owned") — the same SHA-1 appears in two DatGames (two DAT revisions),
    //                       both linked to a RomFile → distinct-owned 1/1.
    //   Bios 2 ("missing") — one DatGame, one DatRom, no RomFile link → 0/1.
    private static void SeedOwnershipGraph(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var ownedSha1 = Sha1.Parse("11" + new string('0', 38));
        var missingSha1 = Sha1.Parse("22" + new string('0', 38));

        db.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = Sha256.Parse("01" + new string('0', 62)),
            Size = 512,
            SizeOnDisk = 256, // compressed on disk — distinct from the DAT-declared 512
            IsCompressed = true,
            CreatedAt = now
        });

        db.RomFiles.Add(new RomFileEntity
        {
            Id = 1,
            FileId = 1,
            OriginalFilename = "scph1001.bin",
            Sha1 = ownedSha1,
            Md5 = Md5.Parse("11" + new string('0', 30)),
            Crc32 = Crc32.Parse("11" + new string('0', 6)),
            CreatedAt = now
        });

        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "PSX DAT",
            Description = "PSX DAT",
            Type = "Unknown",
            PlatformId = 1,
            OriginalFilename = "psx.dat",
            FileId = 1,
            GameCount = 3,
            RomCount = 3,
            CreatedAt = now
        });

        db.SourceEntries.AddRange(
            new SourceEntryEntity { Id = 1, CatalogSourceId = 1, EntryKey = "[BIOS] PSX (USA) (v1)", Name = "[BIOS] PSX (USA) (v1)", PlatformId = 1, CreatedAt = now },
            new SourceEntryEntity { Id = 2, CatalogSourceId = 1, EntryKey = "[BIOS] PSX (USA) (v2)", Name = "[BIOS] PSX (USA) (v2)", PlatformId = 1, CreatedAt = now },
            new SourceEntryEntity { Id = 3, CatalogSourceId = 1, EntryKey = "[BIOS] PSX (Europe)", Name = "[BIOS] PSX (Europe)", PlatformId = 1, CreatedAt = now });
        db.DatGames.AddRange(
            new DatGameEntity { Id = 1, DatFileId = 1, SourceEntryId = 1, Name = "[BIOS] PSX (USA) (v1)", IsBios = true, CreatedAt = now },
            new DatGameEntity { Id = 2, DatFileId = 1, SourceEntryId = 2, Name = "[BIOS] PSX (USA) (v2)", IsBios = true, CreatedAt = now },
            new DatGameEntity { Id = 3, DatFileId = 1, SourceEntryId = 3, Name = "[BIOS] PSX (Europe)", IsBios = true, CreatedAt = now });

        db.Bios.AddRange(
            new BiosEntity { Id = 1, PlatformId = 1, Name = "[BIOS] PSX (USA) (v1)", NormalizedName = "psxusa", CreatedAt = now },
            new BiosEntity { Id = 2, PlatformId = 1, Name = "[BIOS] PSX (Europe)", NormalizedName = "psxeurope", CreatedAt = now });

        db.BiosGameMappings.AddRange(
            new BiosGameMappingEntity { DatGameId = 1, BiosId = 1, CreatedAt = now },
            new BiosGameMappingEntity { DatGameId = 2, BiosId = 1, CreatedAt = now },
            new BiosGameMappingEntity { DatGameId = 3, BiosId = 2, CreatedAt = now });

        db.DatRoms.AddRange(
            new DatRomEntity { Id = 1, DatGameId = 1, Name = "scph1001.bin", Size = 512, Sha1 = ownedSha1, RomFileId = 1, CreatedAt = now },
            new DatRomEntity { Id = 2, DatGameId = 2, Name = "scph1001.bin", Size = 512, Sha1 = ownedSha1, RomFileId = 1, CreatedAt = now },
            new DatRomEntity { Id = 3, DatGameId = 3, Name = "scph1002.bin", Size = 512, Sha1 = missingSha1, RomFileId = null, CreatedAt = now });
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    public void Dispose() => _connection.Dispose();
}
