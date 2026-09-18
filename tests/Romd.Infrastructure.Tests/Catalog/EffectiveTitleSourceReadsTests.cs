using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Source.Rom;
using Romd.Consumer.Application.Browse;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

/// <summary>
///     Characterizes the effective-read boundary: a non-Active catalog source's entries and
///     links vanish from every effective read while the entry-level links (truth) stay
///     untouched, so re-enabling restores visibility with no re-derivation.
///     World: one platform, source A (stays Active) and source D (flipped per test), each
///     backing its own owned title through one entry, game, and stored rom.
/// </summary>
public sealed class EffectiveTitleSourceReadsTests : IDisposable
{
    private const int PlatformId = 1;
    private const int SourceAId = 1;
    private const int SourceDId = 2;
    private const int DatFileAId = 1;
    private const int DatFileDId = 2;
    private const int EntryAId = 1;
    private const int EntryDId = 2;
    private const int GameAId = 1;
    private const int GameDId = 2;
    private const int TitleAId = 101;
    private const int TitleDId = 102;
    private const int RomFileAId = 11;
    private const int RomFileDId = 12;

    // Per-test extras (seeded only by the tests that need them, never by the shared world).
    private const int UnlinkedEntryId = 3;
    private const int UnlinkedGameId = 3;
    private const int UnlinkedRomFileId = 13;
    private const int BiosEntryId = 4;
    private const int BiosGameId = 4;
    private const int BiosId = 900;

    private static Sha1 ShaA => NewSha1(0xA1);
    private static Sha1 ShaD => NewSha1(0xD1);
    private static Sha1 ShaUnlinked => NewSha1(0xE1);
    private static Sha1 ShaBios => NewSha1(0xB1);

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public EffectiveTitleSourceReadsTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        Seed(db);
    }

    [Theory]
    [InlineData(nameof(CatalogSourceStatus.Disabled))]
    [InlineData(nameof(CatalogSourceStatus.Discontinued))]
    public async Task TitleOwnershipReads_NonActiveSource_TitleNoLongerOwned(string status)
    {
        await SetSourceStatusAsync(SourceDId, status);
        await using var db = CreateDb();
        var repository = new TitleRepository(db);

        var ownedByPlatform = await repository.GetWithLocalPayloadByPlatformAsync(PlatformId);
        ownedByPlatform.Select(t => t.Id).ShouldBe([TitleAId]);

        var ownedIds = await repository.GetTitleIdsWithLocalPayloadAsync([TitleAId, TitleDId]);
        ownedIds.ShouldBe(new HashSet<int> { TitleAId });

        (await repository.HasLocalPayloadAsync(TitleAId)).ShouldBeTrue();
        (await repository.HasLocalPayloadAsync(TitleDId)).ShouldBeFalse();
    }

    [Fact]
    public async Task RomCatalogStatus_DisabledSource_RomBecomesUnrouted()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var repository = new RomRepository(db);

        var cataloged = await repository.GetByStatusAsync(RomCatalogStatus.Cataloged);
        cataloged.Items.Select(r => r.Id).ShouldBe([RomFileAId]);

        var unrouted = await repository.GetByStatusAsync(RomCatalogStatus.Unrouted);
        unrouted.Items.Select(r => r.Id).ShouldBe([RomFileDId]);
    }

    [Fact]
    public async Task EnrichmentEvidence_DisabledSource_ReadsEmpty()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var reader = new TitleEnrichmentEvidenceReader(db);

        (await reader.ReadAsync(TitleDId)).ShouldBeEmpty();
        (await reader.ReadAsync(TitleAId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task RomCatalogOwnership_DisabledSource_NoTitleMatch()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var reader = new RomCatalogOwnershipReader(db);

        var match = await reader.ReadAsync(ShaD);
        match.TitleIds.ShouldBeEmpty();
        match.TitlePlatformIds.ShouldBeEmpty();
        match.HasCatalogMatch.ShouldBeFalse();

        (await reader.ReadTitlePlatformIdsAsync(RomFileDId)).ShouldBeEmpty();

        var activeMatch = await reader.ReadAsync(ShaA);
        activeMatch.TitleIds.ShouldBe([TitleAId]);
    }

    [Fact]
    public async Task RomCatalogOwnership_DisabledSource_NoBiosMatch()
    {
        await SeedBiosOnSourceDAsync();

        await using (var db = CreateDb())
        {
            var control = await new RomCatalogOwnershipReader(db).ReadAsync(ShaBios);
            control.BiosPlatformIds.ShouldBe([PlatformId]);
            control.HasCatalogMatch.ShouldBeTrue();
            control.PrimaryPlatformId.ShouldBe(PlatformId);
        }

        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));

        await using (var db = CreateDb())
        {
            var match = await new RomCatalogOwnershipReader(db).ReadAsync(ShaBios);
            match.BiosPlatformIds.ShouldBeEmpty();
            match.HasCatalogMatch.ShouldBeFalse();
            match.PrimaryPlatformId.ShouldBeNull();
        }
    }

    [Fact]
    public async Task RomAccessibility_UnidentifiedRom_AccessibleInValidLibrary()
    {
        int libraryId = await SeedValidLibraryAsync();
        await SeedUnlinkedRomAsync();
        await using var db = CreateDb();
        var repository = new RomRepository(db);

        (await repository.IsAccessibleAsync(UnlinkedRomFileId, libraryId)).ShouldBeTrue();
    }

    [Fact]
    public async Task RomAccessibility_DormantRom_FailsClosedWithoutMaterializedRelease()
    {
        // Dormant = linked to a title at truth level, but every backing source non-Active. That
        // is not "unidentified": authorization must fall through to the materialized-library
        // check and fail closed when no exposed release covers the ROM.
        int libraryId = await SeedValidLibraryAsync();
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var repository = new RomRepository(db);

        (await repository.IsAccessibleAsync(RomFileDId, libraryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task RomAccessibility_DormantRomWithExposedMaterializedRelease_Accessible()
    {
        int libraryId = await SeedValidLibraryAsync();
        await SeedExposedMaterializedReleaseAsync(libraryId);
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var repository = new RomRepository(db);

        (await repository.IsAccessibleAsync(RomFileDId, libraryId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Export_DisabledSource_TitleFilesAbsent()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var repository = new ExportRepository(db, Substitute.For<IConsumerReleaseSelector>());

        var allFiles = await repository.GetExportFilesAsync(new ExportScope.AllCatalog());
        allFiles.Select(f => f.TitleId).ShouldBe([TitleAId]);

        (await repository.GetExportFilesForTitleAsync(
            TitleDId,
            new AuthorizedExportScope(new ExportScope.AllCatalog(), EffectiveLibraryUserId: null)))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Materialization_DisabledSource_NoCandidateRows()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var provider = new MaterializationDataProvider(db);

        var candidates = await provider.GetCandidatesAsync(new LibraryConfiguration());

        candidates.Select(c => c.TitleId).ShouldBe([TitleAId]);
    }

    [Fact]
    public async Task Projection_DisableThenRebuild_RemovesDisabledSourcesRelease()
    {
        await using (var db = CreateDb())
        {
            (await CreateProjectionService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
            (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(2);
        }

        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));

        await using (var db = CreateDb())
        {
            (await CreateProjectionService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

            var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
            release.PrimarySha1.ShouldBe(ShaA);
        }
    }

    [Fact]
    public async Task OwnershipRead_ReEnabledSource_RestoresWithoutRederivation()
    {
        await using var db = CreateDb();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(2);

        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        (await new TitleRepository(db).HasLocalPayloadAsync(TitleDId)).ShouldBeFalse();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(2);

        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Active));
        (await new TitleRepository(db).HasLocalPayloadAsync(TitleDId)).ShouldBeTrue();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task TruthLevelReads_DisabledSource_TitleStaysDormantWithItsGame()
    {
        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));
        await using var db = CreateDb();
        var repository = new TitleRepository(db);

        (await repository.HasGamesAsync(TitleDId)).ShouldBeTrue();

        var detail = await repository.GetTitleDetailAsync(TitleDId);
        detail.ShouldNotBeNull();
        detail.Releases.Count.ShouldBe(1);
        detail.Releases[0].Id.ShouldBe(GameDId);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static CatalogProjectionService CreateProjectionService(RomdDbContext db) =>
        CatalogProjectionTestFactory.Create(db);

    private async Task SetSourceStatusAsync(int catalogSourceId, string status)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.CatalogSources
            .Where(s => s.Id == catalogSourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Status, status));
        var titleIds = await db.TitleSourceLinks
            .Where(link => db.SourceEntries.Any(entry =>
                entry.Id == link.SourceEntryId && entry.CatalogSourceId == catalogSourceId))
            .Select(link => link.TitleId)
            .ToListAsync();
        await new TitlePayloadAvailabilityProjection(
                db,
                new CatalogPayloadAssertionReader(db, [new DatCatalogPayloadAssertionProvider(db)]))
            .RefreshPayloadAssertionsAsync(titleIds);
        await transaction.CommitAsync();
    }

    private async Task<int> SeedValidLibraryAsync()
    {
        await using var db = CreateDb();
        var library = new LibraryEntity { Name = "Library", ConfigurationJson = "{}" };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    /// <summary>A stored rom whose entry (under the Active source A) has no title link.</summary>
    private async Task SeedUnlinkedRomAsync()
    {
        await using var db = CreateDb();
        db.Files.Add(NewFile(UnlinkedRomFileId));
        db.SourceEntries.Add(NewSourceEntry(UnlinkedEntryId, SourceAId));
        db.DatGames.Add(NewGame(UnlinkedGameId, DatFileAId, UnlinkedEntryId));
        db.DatRoms.Add(NewDatRom(3, UnlinkedGameId, ShaUnlinked, UnlinkedRomFileId));
        db.RomFiles.Add(NewRomFile(UnlinkedRomFileId, ShaUnlinked, 0xE1));
        await db.SaveChangesAsync();
    }

    private async Task SeedExposedMaterializedReleaseAsync(int libraryId)
    {
        await using var db = CreateDb();
        db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = libraryId,
            TitleId = TitleDId,
            DatGameId = GameDId,
            DatFileId = DatFileDId,
            PlatformId = PlatformId,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = true,
            IsExposed = true
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A BIOS game under source D: entry, game, rom, BIOS group, and mapping.</summary>
    private async Task SeedBiosOnSourceDAsync()
    {
        await using var db = CreateDb();
        db.SourceEntries.Add(NewSourceEntry(BiosEntryId, SourceDId));
        db.DatGames.Add(NewGame(BiosGameId, DatFileDId, BiosEntryId, isBios: true));
        db.DatRoms.Add(NewDatRom(4, BiosGameId, ShaBios, romFileId: null));
        db.Bios.Add(new BiosEntity
        {
            Id = BiosId,
            PlatformId = PlatformId,
            Name = "Console BIOS",
            NormalizedName = "consolebios"
        });
        db.BiosGameMappings.Add(new BiosGameMappingEntity { DatGameId = BiosGameId, BiosId = BiosId });
        await db.SaveChangesAsync();
    }

    private static void Seed(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Nintendo 64",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = "n64", ShortName = "n64"
        });

        db.Files.AddRange(
            NewFile(DatFileAId),
            NewFile(DatFileDId),
            NewFile(RomFileAId),
            NewFile(RomFileDId));

        SeedSource(db, SourceAId, DatFileAId);
        SeedSource(db, SourceDId, DatFileDId);

        db.Titles.AddRange(
            NewTitle(TitleAId),
            NewTitle(TitleDId));

        db.SourceEntries.AddRange(
            NewSourceEntry(EntryAId, SourceAId),
            NewSourceEntry(EntryDId, SourceDId));

        db.DatGames.AddRange(
            NewGame(GameAId, DatFileAId, EntryAId),
            NewGame(GameDId, DatFileDId, EntryDId));

        db.DatRoms.AddRange(
            NewDatRom(1, GameAId, ShaA, RomFileAId),
            NewDatRom(2, GameDId, ShaD, RomFileDId));

        db.RomFiles.AddRange(
            NewRomFile(RomFileAId, ShaA, 0xA1),
            NewRomFile(RomFileDId, ShaD, 0xD1));

        db.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = EntryAId, TitleId = TitleAId },
            new TitleSourceLinkEntity { SourceEntryId = EntryDId, TitleId = TitleDId });

        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static void SeedSource(RomdDbContext db, int catalogSourceId, int datFileId) =>
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
            Name = $"DAT {datFileId}",
            Description = $"DAT {datFileId}",
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = $"dat-{datFileId}.dat",
            FileId = datFileId
        });

    private static FileEntityPersistence NewFile(byte value) => new()
    {
        Id = value,
        Sha256 = NewSha256(value),
        Size = 1,
        SizeOnDisk = 1
    };

    private static TitleEntity NewTitle(int id) => new()
    {
        Id = id,
        PlatformId = PlatformId,
        HasLocalPayload = true,
        Name = $"Title {id}",
        NormalizedName = $"title{id}",
        EnrichmentStatus = "None"
    };

    private static SourceEntryEntity NewSourceEntry(int id, int catalogSourceId) => new()
    {
        Id = id,
        CatalogSourceId = catalogSourceId,
        EntryKey = $"Game {id}",
        Name = $"Game {id}",
        PlatformId = PlatformId,
        HasLocalPayload = true
    };

    private static DatGameEntity NewGame(int id, int datFileId, int sourceEntryId, bool isBios = false) => new()
    {
        Id = id,
        DatFileId = datFileId,
        SourceEntryId = sourceEntryId,
        Name = $"Game {id}",
        IsBios = isBios
    };

    private static DatRomEntity NewDatRom(int id, int datGameId, Sha1 sha1, int? romFileId) => new()
    {
        Id = id,
        DatGameId = datGameId,
        Name = $"game-{id}.rom",
        Size = 1024,
        Sha1 = sha1,
        RomFileId = romFileId
    };

    private static RomFileEntity NewRomFile(int id, Sha1 sha1, byte seed) => new()
    {
        Id = id,
        OriginalFilename = $"rom-{id}.z64",
        FileId = id,
        Sha1 = sha1,
        Md5 = NewMd5(seed),
        Crc32 = NewCrc32(seed)
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
