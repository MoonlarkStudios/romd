using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed class TitleSourceReferenceReaderTests : IDisposable
{
    private const int PlatformId = 1;
    private const int TitleId = 10;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TitleSourceReferenceReaderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedPlatformAndTitle(db);
    }

    [Fact]
    public async Task GetReferencesAsync_TitleBackedByTwoSources_ReturnsTruthLevelReferencesOrderedById()
    {
        await using var db = CreateDb();
        SeedDatSource(db, catalogSourceId: 1, datFileId: 1, datName: "No-Intro N64");
        SeedEntryWithLink(db, entryId: 1, catalogSourceId: 1);
        SeedEntryWithLink(db, entryId: 2, catalogSourceId: 1);
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 2,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Disabled),
            Name = "Bulk Import"
        });
        SeedEntryWithLink(db, entryId: 3, catalogSourceId: 2);
        await db.SaveChangesAsync();

        var references = await new TitleSourceReferenceReader(db).GetReferencesAsync(TitleId);

        references.Count.ShouldBe(2);
        references[0].ShouldBe(new TitleSourceReference(
            1, CatalogSourceKind.Dat, "No-Intro N64", CatalogSourceStatus.Active, EntryCount: 2, DatId: 1, PlatformId: 1, HasActiveDefinition: false));
        references[1].ShouldBe(new TitleSourceReference(
            2, CatalogSourceKind.Import, "Bulk Import", CatalogSourceStatus.Disabled, EntryCount: 1));
    }

    [Fact]
    public async Task GetReferencesAsync_DatSourceWithoutActiveVersion_NameIsNull()
    {
        await using var db = CreateDb();
        SeedDatSource(
            db,
            catalogSourceId: 1,
            datFileId: 1,
            datName: "Old N64",
            lifecycle: nameof(DatFileLifecycle.Superseded));
        SeedEntryWithLink(db, entryId: 1, catalogSourceId: 1);
        await db.SaveChangesAsync();

        var references = await new TitleSourceReferenceReader(db).GetReferencesAsync(TitleId);

        var reference = references.ShouldHaveSingleItem();
        reference.Kind.ShouldBe(CatalogSourceKind.Dat);
        reference.Name.ShouldBeNull();
    }

    [Fact]
    public async Task GetReferencesAsync_UnbackedTitle_ReturnsEmpty()
    {
        await using var db = CreateDb();

        var references = await new TitleSourceReferenceReader(db).GetReferencesAsync(TitleId);

        references.ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void SeedPlatformAndTitle(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Nintendo 64",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = "n64", ShortName = "n64"
        });
        db.Titles.Add(new TitleEntity
        {
            Id = TitleId,
            PlatformId = PlatformId,
            Name = $"Title {TitleId}",
            NormalizedName = $"title{TitleId}",
            EnrichmentStatus = "None"
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static void SeedDatSource(
        RomdDbContext db,
        int catalogSourceId,
        int datFileId,
        string datName,
        string lifecycle = nameof(DatFileLifecycle.Active))
    {
        db.Files.Add(new FileEntityPersistence
        {
            Id = datFileId,
            Sha256 = NewSha256((byte)datFileId),
            Size = 1,
            SizeOnDisk = 1
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity
                {
                    Id = catalogSourceId,
                    Kind = nameof(CatalogSourceKind.Dat),
                    Status = nameof(CatalogSourceStatus.Active)
                }
            },
            Id = datFileId,
            Name = datName,
            Description = datName,
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = $"dat-{datFileId}.dat",
            FileId = datFileId,
            Lifecycle = lifecycle
        });
    }

    private static void SeedEntryWithLink(RomdDbContext db, int entryId, int catalogSourceId)
    {
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = entryId,
            CatalogSourceId = catalogSourceId,
            EntryKey = $"Game {entryId}",
            Name = $"Game {entryId}",
            PlatformId = PlatformId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = entryId,
            TitleId = TitleId
        });
    }

    private static Sha256 NewSha256(byte value)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = value;
        return Sha256.FromBytes(bytes);
    }
}
