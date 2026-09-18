using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Enrichment;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class TitleEnrichmentEvidenceReaderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TitleEnrichmentEvidenceReaderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedEvidence(db);
    }

    [Fact]
    public async Task ReadAsync_MappedTitle_ReturnsStableNeutralEvidenceAndExcludesBios()
    {
        await using var db = CreateDb();
        var reader = new TitleEnrichmentEvidenceReader(db);

        var result = await reader.ReadAsync(201);

        result.Select(candidate => candidate.Name).ShouldBe(["Game First", "Game Later"]);

        var first = result[0];
        first.Year.ShouldBe("1990");
        first.Manufacturer.ShouldBe("Studio");
        first.Region.ShouldBe("USA");
        first.Revision.ShouldBe("Rev 1");
        first.DevelopmentStatus.ShouldBe("Beta");
        first.HasDefinedRoms.ShouldBeTrue();
        first.HasOwnedRom.ShouldBeTrue();
        first.OwnedRomHashes.Count.ShouldBe(2);

        var ownedWithoutHashes = first.OwnedRomHashes[0];
        ownedWithoutHashes.Sha1.ShouldBeNull();
        ownedWithoutHashes.Md5.ShouldBeNull();
        ownedWithoutHashes.Crc32.ShouldBeNull();
        ownedWithoutHashes.Size.ShouldBe(123);

        var ownedWithHashes = first.OwnedRomHashes[1];
        ownedWithHashes.Sha1.ShouldBe(NewSha1(9));
        ownedWithHashes.Md5.ShouldBe(NewMd5(9));
        ownedWithHashes.Crc32.ShouldBe(NewCrc32(9));
        ownedWithHashes.Size.ShouldBe(456);

        var later = result[1];
        later.HasDefinedRoms.ShouldBeFalse();
        later.HasOwnedRom.ShouldBeFalse();
        later.OwnedRomHashes.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_UnmappedTitle_ReturnsEmptyEvidence()
    {
        await using var db = CreateDb();
        var reader = new TitleEnrichmentEvidenceReader(db);

        var result = await reader.ReadAsync(999);

        result.ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void SeedEvidence(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test Platform", BaseCompactLabel = "Test Platform", CanonicalKey = "test", ShortName = "test"
        });
        db.Files.AddRange(
            NewFile(1),
            NewFile(50),
            NewFile(51));
        db.DatSources.Add(new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = 1,
            DatSourceId = 1,
            Name = "Test DAT",
            Description = "Test DAT",
            Type = "NoIntro",
            PlatformId = 7,
            OriginalFilename = "test.dat",
            FileId = 1
        });
        db.Titles.AddRange(
            NewTitle(201, "Target Title"),
            NewTitle(202, "Other Title"));
        db.RomFiles.AddRange(
            NewRomFile(50),
            NewRomFile(51));
        db.SourceEntries.AddRange(
            NewSourceEntry(20, "Game Later"),
            NewSourceEntry(15, "Game First"),
            NewSourceEntry(10, "[BIOS] System"),
            NewSourceEntry(30, "Other Title Game"));
        db.DatGames.AddRange(
            new DatGameEntity { Id = 20, DatFileId = 1, SourceEntryId = 20, Name = "Game Later", Year = "1991" },
            new DatGameEntity
            {
                Id = 15,
                DatFileId = 1,
                SourceEntryId = 15,
                Name = "Game First",
                Year = "1990",
                Manufacturer = "Studio",
                Region = "USA",
                Revision = "Rev 1",
                DevelopmentStatus = "Beta"
            },
            new DatGameEntity { Id = 10, DatFileId = 1, SourceEntryId = 10, Name = "[BIOS] System", IsBios = true },
            new DatGameEntity { Id = 30, DatFileId = 1, SourceEntryId = 30, Name = "Other Title Game" });
        db.DatRoms.AddRange(
            new DatRomEntity
            {
                Id = 100,
                DatGameId = 15,
                Name = "unowned.rom",
                Size = 100,
                Sha1 = NewSha1(8)
            },
            new DatRomEntity
            {
                Id = 90,
                DatGameId = 15,
                Name = "owned-no-hashes.rom",
                Size = 123,
                RomFileId = 50
            },
            new DatRomEntity
            {
                Id = 95,
                DatGameId = 15,
                Name = "owned-with-hashes.rom",
                Size = 456,
                Sha1 = NewSha1(9),
                Md5 = NewMd5(9),
                Crc = NewCrc32(9),
                RomFileId = 51
            },
            new DatRomEntity
            {
                Id = 80,
                DatGameId = 10,
                Name = "bios.rom",
                Size = 1,
                RomFileId = 50
            });
        db.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = 20, TitleId = 201 },
            new TitleSourceLinkEntity { SourceEntryId = 15, TitleId = 201 },
            new TitleSourceLinkEntity { SourceEntryId = 10, TitleId = 201 },
            new TitleSourceLinkEntity { SourceEntryId = 30, TitleId = 202 });

        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static FileEntityPersistence NewFile(byte value) => new()
    {
        Id = value,
        Sha256 = NewSha256(value),
        Size = 1,
        SizeOnDisk = 1
    };

    private static TitleEntity NewTitle(int id, string name) => new()
    {
        Id = id,
        PlatformId = 7,
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        EnrichmentStatus = "None"
    };

    private static SourceEntryEntity NewSourceEntry(int id, string name) => new()
    {
        Id = id,
        CatalogSourceId = 1,
        EntryKey = name,
        Name = name,
        PlatformId = 7
    };

    private static RomFileEntity NewRomFile(byte value) => new()
    {
        Id = value,
        OriginalFilename = $"owned-{value}.rom",
        FileId = value,
        Sha1 = NewSha1(value),
        Md5 = NewMd5(value),
        Crc32 = NewCrc32(value)
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
