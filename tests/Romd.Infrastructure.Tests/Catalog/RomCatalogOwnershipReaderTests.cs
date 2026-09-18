using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed class RomCatalogOwnershipReaderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public RomCatalogOwnershipReaderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        Seed(db);
    }

    [Fact]
    public async Task ReadAsync_MatchingHash_ReturnsDistinctTitleAndBiosCatalogMeaning()
    {
        await using var db = CreateDb();
        var reader = new RomCatalogOwnershipReader(db);

        var match = await reader.ReadAsync(NewSha1(1));

        match.TitleIds.Order().ShouldBe([101, 102]);
        match.TitlePlatformIds.Order().ShouldBe([10, 20]);
        match.BiosPlatformIds.ShouldBe([30]);
        match.HasCatalogMatch.ShouldBeTrue();
        match.PrimaryPlatformId.ShouldBeOneOf(10, 20);
    }

    [Fact]
    public async Task ReadTitlePlatformIdsAsync_StoredRom_ReturnsOnlyActuallyLinkedTitlePlatforms()
    {
        await using var db = CreateDb();
        var reader = new RomCatalogOwnershipReader(db);

        var platformIds = await reader.ReadTitlePlatformIdsAsync(50);

        platformIds.ShouldBe([10]);
    }

    [Fact]
    public async Task ReadAsync_BiosOnlyHash_ReturnsBiosPlatformWithoutTitleMatch()
    {
        await using var db = CreateDb();
        var reader = new RomCatalogOwnershipReader(db);

        var match = await reader.ReadAsync(NewSha1(2));

        match.TitleIds.ShouldBeEmpty();
        match.TitlePlatformIds.ShouldBeEmpty();
        match.BiosPlatformIds.ShouldBe([30]);
        match.HasCatalogMatch.ShouldBeTrue();
        match.PrimaryPlatformId.ShouldBe(30);
    }

    [Fact]
    public async Task ReadAsync_UnknownHash_ReturnsNoCatalogMatch()
    {
        await using var db = CreateDb();
        var reader = new RomCatalogOwnershipReader(db);

        var match = await reader.ReadAsync(NewSha1(9));

        match.TitleIds.ShouldBeEmpty();
        match.TitlePlatformIds.ShouldBeEmpty();
        match.BiosPlatformIds.ShouldBeEmpty();
        match.HasCatalogMatch.ShouldBeFalse();
        match.PrimaryPlatformId.ShouldBeNull();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void Seed(RomdDbContext db)
    {
        db.Platforms.AddRange(
            NewPlatform(10),
            NewPlatform(20),
            NewPlatform(30));
        db.Files.AddRange(NewFile(1), NewFile(50));
        db.RomFiles.Add(new RomFileEntity
        {
            Id = 50,
            OriginalFilename = "owned.rom",
            FileId = 50,
            Sha1 = NewSha1(1),
            Md5 = NewMd5(1),
            Crc32 = NewCrc32(1)
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "Catalog DAT",
            Description = "Catalog DAT",
            Type = "NoIntro",
            PlatformId = 10,
            OriginalFilename = "catalog.dat",
            FileId = 1
        });
        db.Titles.AddRange(
            NewTitle(101, 10),
            NewTitle(102, 20));
        db.SourceEntries.AddRange(
            NewSourceEntry(1),
            NewSourceEntry(2),
            NewSourceEntry(3),
            NewSourceEntry(4));
        db.DatGames.AddRange(
            NewGame(1),
            NewGame(2),
            NewGame(3, isBios: true),
            NewGame(4, isBios: true));
        db.DatRoms.AddRange(
            NewDatRom(1, 1, romFileId: 50),
            NewDatRom(2, 1, romFileId: 50),
            NewDatRom(3, 2),
            NewDatRom(4, 3),
            NewDatRom(5, 4, sha1: NewSha1(2)));
        db.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = 1, TitleId = 101 },
            new TitleSourceLinkEntity { SourceEntryId = 2, TitleId = 102 });
        db.Bios.Add(new BiosEntity
        {
            Id = 30,
            PlatformId = 30,
            Name = "Test BIOS",
            NormalizedName = "testbios"
        });
        db.BiosGameMappings.AddRange(
            new BiosGameMappingEntity { DatGameId = 3, BiosId = 30 },
            new BiosGameMappingEntity { DatGameId = 4, BiosId = 30 });

        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static PlatformEntity NewPlatform(int id) => new()
    {
        Id = id,
        Name = $"Platform {id}",
        ShortName = $"p{id}"
    };

    private static FileEntityPersistence NewFile(byte value) => new()
    {
        Id = value,
        Sha256 = NewSha256(value),
        Size = 1,
        SizeOnDisk = 1
    };

    private static TitleEntity NewTitle(int id, int platformId) => new()
    {
        Id = id,
        PlatformId = platformId,
        Name = $"Title {id}",
        NormalizedName = $"title{id}",
        EnrichmentStatus = "None"
    };

    private static SourceEntryEntity NewSourceEntry(int id) => new()
    {
        Id = id,
        CatalogSourceId = 1,
        EntryKey = $"Game {id}",
        Name = $"Game {id}",
        PlatformId = 10
    };

    private static DatGameEntity NewGame(int id, bool isBios = false) => new()
    {
        Id = id,
        DatFileId = 1,
        SourceEntryId = id,
        Name = $"Game {id}",
        IsBios = isBios
    };

    private static DatRomEntity NewDatRom(
        int id,
        int datGameId,
        int? romFileId = null,
        Sha1? sha1 = null) => new()
    {
        Id = id,
        DatGameId = datGameId,
        Name = $"game-{id}.rom",
        Size = 1,
        Sha1 = sha1 ?? NewSha1(1),
        RomFileId = romFileId
    };

    private static Sha256 NewSha256(byte value) => Sha256.FromBytes(NewBytes(Sha256.ByteLength, value));
    private static Sha1 NewSha1(byte value) => Sha1.FromBytes(NewBytes(Sha1.ByteLength, value));
    private static Md5 NewMd5(byte value) => Md5.FromBytes(NewBytes(Md5.ByteLength, value));
    private static Crc32 NewCrc32(byte value) => Crc32.FromBytes(NewBytes(Crc32.ByteLength, value));

    private static byte[] NewBytes(int length, byte value)
    {
        var bytes = new byte[length];
        bytes[0] = value;
        return bytes;
    }
}
