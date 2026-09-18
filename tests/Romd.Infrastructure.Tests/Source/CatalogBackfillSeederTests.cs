using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class CatalogBackfillSeederTests : IDisposable
{
    private const int PlatformId = 1;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public CatalogBackfillSeederTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task BackfillAsync_UnbuiltPlatform_BuildsCatalogAndFlagsLibraries()
    {
        using var db = CreateDb();
        SeedMappedGame(db);
        SeedLibrary(db, needsMaterialization: false);
        await db.SaveChangesAsync();

        await RunBackfillAsync(db);

        (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(1);
        var platform = await db.Platforms.AsNoTracking().SingleAsync(p => p.Id == PlatformId);
        platform.CatalogRebuiltAt.ShouldNotBeNull();
        var library = await db.Libraries.AsNoTracking().SingleAsync();
        library.NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task BackfillAsync_AlreadyBuiltPlatform_IsNoOpAndDoesNotReflagLibraries()
    {
        using var db = CreateDb();
        SeedMappedGame(db);
        SeedLibrary(db, needsMaterialization: false);
        await db.SaveChangesAsync();

        // First run builds the catalog and stamps CatalogRebuiltAt.
        await RunBackfillAsync(db);

        // Reset the library flag and re-run; the built platform must be skipped (no reflagging).
        await db.Libraries.ExecuteUpdateAsync(s => s.SetProperty(l => l.NeedsMaterialization, false));
        await RunBackfillAsync(db);

        var library = await db.Libraries.AsNoTracking().SingleAsync();
        library.NeedsMaterialization.ShouldBeFalse();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static Task RunBackfillAsync(RomdDbContext db) =>
        CatalogBackfillSeeder.BackfillAsync(
            db,
            CatalogProjectionTestFactory.Create(db),
            new LibraryRepository(db),
            NullLogger.Instance,
            CancellationToken.None);

    private static void SeedLibrary(RomdDbContext db, bool needsMaterialization)
    {
        db.Libraries.Add(new LibraryEntity
        {
            Name = "Living Room",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = needsMaterialization,
            ItemCount = 0,
            IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemActor.UserId
        });
    }

    private static void SeedMappedGame(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = SystemActor.UserId;

        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 10,
            PlatformId = PlatformId,
            Name = "Title 10",
            NormalizedName = "title 10",
            EnrichmentStatus = "None",
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
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "DAT 1",
            Description = "DAT 1",
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = "dat-1.dat",
            FileId = 1,
            GameCount = 1,
            RomCount = 1,
            DiskCount = 0,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1,
            CatalogSourceId = 1,
            EntryKey = "Game 1",
            Name = "Game 1",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = 1,
            DatFileId = 1,
            SourceEntryId = 1,
            Name = "Game 1",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = 1,
            DatGameId = 1,
            Name = "game-1.sfc",
            Size = 1024,
            Sha1 = NewSha1(0x11),
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 1,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = userId
        });
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }
}
