using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Verifies that collection coverage is scoped to tracked titles: the denominator is the
///     tracked set and the numerator is tracked titles that are owned.
/// </summary>
public sealed class RomRepositoryCoverageTests : IDisposable
{
    private const int PlatformId = 1;
    private readonly RecordingCommandInterceptor _commands = new();
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public RomRepositoryCoverageTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(_commands)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task GetCoverageBreakdownAsync_CountsOnlyTrackedTitles()
    {
        using var db = CreateDb();
        await SeedCoverageGraphAsync(db);
        var repository = new RomRepository(db);

        var coverage = await repository.GetCoverageBreakdownAsync();

        // Tracked: title 1 (owned) + title 2 (unowned). Title 3 is owned but untracked → excluded.
        coverage.ExpectedTitleCount.ShouldBe(2);
        coverage.LocalPayloadTitleCount.ShouldBe(1);
        coverage.CompleteTitleCount.ShouldBe(1);
        coverage.PartialTitleCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatsAsync_PlatformBreakdown_CountsOnlyTrackedTitles()
    {
        using var db = CreateDb();
        await SeedCoverageGraphAsync(db);
        var repository = new RomRepository(db);

        var stats = await repository.GetStatsAsync();

        var platform = stats.PlatformBreakdown.Single(p => p.PlatformId == PlatformId);
        platform.TotalCount.ShouldBe(2);
        platform.LocalPayloadCount.ShouldBe(1);

        var emptyPlatform = stats.PlatformBreakdown.Single(p => p.PlatformId == 2);
        emptyPlatform.TotalCount.ShouldBe(0);
        emptyPlatform.LocalPayloadCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatsAsync_PlatformBreakdown_UsesSingleTrackedTitleAggregate()
    {
        using var db = CreateDb();
        await SeedCoverageGraphAsync(db);
        _commands.Clear();
        var repository = new RomRepository(db);

        await repository.GetStatsAsync();

        string aggregateSql = _commands.Commands
            .Where(command =>
                command.Contains("GROUP BY", StringComparison.Ordinal) &&
                command.Contains("TrackedTitles", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        aggregateSql.ShouldContain("LEFT JOIN");
        aggregateSql.ShouldNotContain("SELECT COUNT(*)\n    FROM romd.\"Titles\" AS");
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetMatchesAsync_PreservesMultipleTitlesAndUnassignedSourceMatches()
    {
        using var db = CreateDb();
        await SeedCoverageGraphAsync(db);
        var repository = new RomRepository(db);
        var matches = await repository.GetMatchesAsync(1);
        matches.Select(match => match.TitleId).Order().ShouldBe(new int?[] { 1, 3 });
        matches.Select(match => match.DatGameId).Order().ShouldBe(new[] { 1, 3 });
        matches.ShouldAllBe(match => match.PlatformId == PlatformId && match.DatName == "No-Intro SNES");
        await db.TitleSourceLinks.ExecuteDeleteAsync();
        var unassigned = await repository.GetMatchesAsync(1);
        unassigned.Count.ShouldBe(2);
        unassigned.ShouldAllBe(match => match.TitleId == null && match.PlatformId == PlatformId);
        (await repository.GetMatchesAsync(999)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_FilenameSearch_FiltersBeforePaginationAndCombinesWithStatus()
    {
        using var db = CreateDb();
        await SeedCoverageGraphAsync(db);
        db.RomFiles.AddRange(Enumerable.Range(2, 3).Select(id => new RomFileEntity
        {
            Id = id,
            OriginalFilename = id == 3 ? "Other.sfc" : $"Owned_100%_{id}.sfc",
            FileId = 1,
            Sha1 = NewSha1((byte)id),
            Md5 = NewMd5((byte)id),
            Crc32 = Crc32.FromUInt32((uint)id),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        }));
        await db.SaveChangesAsync();
        var repository = new RomRepository(db);

        var first = await repository.ListAsync(limit: 1, search: " OWNED_100% ");
        first.Items.Single().Rom.OriginalFilename.ShouldBe("Owned_100%_2.sfc");
        first.HasNextPage.ShouldBeTrue();
        var next = await repository.ListAsync(cursor: first.NextCursor, limit: 1, search: "owned_100%");
        next.Items.Single().Rom.OriginalFilename.ShouldBe("Owned_100%_4.sfc");
        next.HasNextPage.ShouldBeFalse();
        var cataloged = await repository.ListAsync(RomCatalogStatus.Cataloged, search: "owned");
        cataloged.Items.Single().Rom.OriginalFilename.ShouldBe("owned.sfc");
        var unidentified = await repository.ListAsync(RomCatalogStatus.Unidentified, search: "owned");
        unidentified.Items.Count.ShouldBe(2);
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    /// <summary>
    ///     Seeds a platform with three titles:
    ///     <list type="bullet">
    ///         <item>Title 1 — tracked + owned (its DatRom links a RomFile).</item>
    ///         <item>Title 2 — tracked + unowned (its DatRom has no RomFile).</item>
    ///         <item>Title 3 — untracked + owned.</item>
    ///     </list>
    /// </summary>
    private static async Task SeedCoverageGraphAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo Entertainment System", BaseCompactLabel = "Super Nintendo Entertainment System", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Empty Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Empty Platform", BaseCompactLabel = "Empty Platform", CanonicalKey = "empty", ShortName = "empty",
            Manufacturer = "None",
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
            OriginalFilename = "owned.sfc",
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
            Name = "No-Intro SNES",
            Description = "SNES DAT",
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = "snes.dat",
            FileId = 1,
            GameCount = 4,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        AddTitle(db, id: 1, isTracked: true, hasLocalPayload: true, now, userId);
        AddTitle(db, id: 2, isTracked: true, hasLocalPayload: false, now, userId);
        AddTitle(db, id: 3, isTracked: false, hasLocalPayload: true, now, userId);
        AddTitle(db, id: 4, isTracked: false, hasLocalPayload: false, now, userId);

        // Title 1 — owned (RomFileId set)
        AddGameWithRom(db, gameId: 1, titleId: 1, romFileId: 1, now, userId);
        // Title 2 — tracked but unowned (RomFileId null)
        AddGameWithRom(db, gameId: 2, titleId: 2, romFileId: null, now, userId);
        // Title 3 — untracked but owned
        AddGameWithRom(db, gameId: 3, titleId: 3, romFileId: 1, now, userId);
        // Title 4 — untracked and unavailable
        AddGameWithRom(db, gameId: 4, titleId: 4, romFileId: null, now, userId);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void AddTitle(
        RomdDbContext db,
        int id,
        bool isTracked,
        bool hasLocalPayload,
        DateTimeOffset now,
        Guid userId)
    {
        db.Titles.Add(new TitleEntity
        {
            Id = id,
            PlatformId = PlatformId,
            HasLocalPayload = hasLocalPayload,
            Name = $"Title {id}",
            NormalizedName = $"title{id}",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        if (isTracked)
        {
            db.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = id,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    private static void AddGameWithRom(
        RomdDbContext db,
        int gameId,
        int titleId,
        int? romFileId,
        DateTimeOffset now,
        Guid userId)
    {
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = 1,
            EntryKey = $"Game {gameId}",
            Name = $"Game {gameId}",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = 1,
            SourceEntryId = gameId,
            Name = $"Game {gameId}",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatRoms.Add(new DatRomEntity
        {
            Id = gameId,
            DatGameId = gameId,
            Name = $"game{gameId}.sfc",
            Size = 1,
            Sha1 = NewSha1((byte)gameId),
            RomFileId = romFileId,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = gameId,
            TitleId = titleId,
            CreatedAt = now,
            CreatedByUserId = userId
        });
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

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
