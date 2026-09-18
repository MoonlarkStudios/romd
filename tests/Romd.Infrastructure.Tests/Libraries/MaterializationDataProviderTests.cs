using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class MaterializationDataProviderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly CountingCommandInterceptor _commandInterceptor = new();
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public MaterializationDataProviderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(_commandInterceptor)
            .Options;

        using var db = CreateDb();
        Seed(db);
    }

    [Fact]
    public async Task GetCandidatesAsync_MultipleChildCollections_ProjectsAggregatesInSingleQuery()
    {
        using var db = CreateDb();
        var provider = new MaterializationDataProvider(db);
        var config = new LibraryConfiguration
        {
            AllowedPlatformIds = [1]
        };

        _commandInterceptor.Reset();

        var results = await provider.GetCandidatesAsync(config);

        var title = results.Single();
        var candidate = title.Candidates.Single();

        _commandInterceptor.ReaderCount.ShouldBe(2);
        var command = _commandInterceptor.ReaderCommands.Single(command =>
            command.Contains("DatGameRegions", StringComparison.Ordinal));
        command.ShouldContain("DatGameRegions");
        command.ShouldContain("DatGameLanguages");
        command.ShouldContain("GROUP BY");
        _commandInterceptor.ReaderCommands.ShouldContain(command =>
            command.Contains("TitleContentRatings", StringComparison.Ordinal));
        title.TitleId.ShouldBe(1);
        title.PlatformId.ShouldBe(1);
        var rating = title.ContentRatings.Single();
        rating.Board.ShouldBe(RatingBoard.Esrb);
        rating.Code.ShouldBe("E");
        rating.MinimumAge.ShouldBe(0);
        candidate.DatGameId.ShouldBe(1);
        candidate.HasOwnedRoms.ShouldBeTrue();
        candidate.IsComplete.ShouldBeFalse();
        candidate.RegionIds.ShouldBe([1, 2], ignoreOrder: true);
        candidate.LanguageIds.ShouldBe([1, 2], ignoreOrder: true);
    }

    [Fact]
    public async Task GetCandidatesAsync_RomFacts_UsesSingleGroupedDatRomsAggregate()
    {
        using var db = CreateDb();
        var provider = new MaterializationDataProvider(db);

        _commandInterceptor.Reset();

        await provider.GetCandidatesAsync(new LibraryConfiguration { AllowedPlatformIds = [1] });

        var datRomsCommand = _commandInterceptor.ReaderCommands.Single(command =>
            command.Contains("DatRoms", StringComparison.Ordinal));
        datRomsCommand.ShouldContain("GROUP BY");
    }

    [Fact]
    public async Task GetCandidatesAsync_AllDatGameRomsOwned_ReportsComplete()
    {
        using var db = CreateDb();
        await db.DatRoms
            .Where(r => r.DatGameId == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(r => r.RomFileId, 1));

        var provider = new MaterializationDataProvider(db);
        var results = await provider.GetCandidatesAsync(new LibraryConfiguration { AllowedPlatformIds = [1] });

        var candidate = results.Single().Candidates.Single();
        candidate.HasOwnedRoms.ShouldBeTrue();
        candidate.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCandidatesAsync_DatGameWithoutRoms_ReportsIncomplete()
    {
        using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Titles.Add(new TitleEntity
        {
            Id = 3,
            PlatformId = 1,
            Name = "Empty Game",
            NormalizedName = "empty game",
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 2,
            CatalogSourceId = 1,
            EntryKey = "Empty Game",
            Name = "Empty Game",
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatGames.Add(new DatGameEntity
        {
            Id = 2,
            DatFileId = 1,
            SourceEntryId = 2,
            Name = "Empty Game",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 2,
            TitleId = 3,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        await db.SaveChangesAsync();

        var provider = new MaterializationDataProvider(db);
        var results = await provider.GetCandidatesAsync(new LibraryConfiguration { AllowedPlatformIds = [1] });

        var candidate = results.Single(title => title.TitleId == 3).Candidates.Single();
        candidate.HasOwnedRoms.ShouldBeFalse();
        candidate.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task GetCandidatesAsync_PendingVersionSharesEntry_OnlyAssertingVersionCarriesRelease()
    {
        using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        // A pending replacement version of the same source shares the seeded entry, but the
        // release-source row asserts (entry, ACTIVE file): the pending game must not inherit it.
        db.Files.Add(new FileEntityPersistence
        {
            Id = 2,
            Sha256 = NewSha256(2),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = 2,
            DatSourceId = 1,
            Lifecycle = "PendingActivation",
            Name = "No-Intro NES v2",
            Description = "NES DAT v2",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "nes-v2.dat",
            FileId = 2,
            GameCount = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = 2,
            DatFileId = 2,
            SourceEntryId = 1,
            Name = "Mega Game",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 500,
            PlatformId = 1,
            CatalogTitleId = 1,
            Fingerprint = "sha1:mega-game",
            Name = "Mega Game",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId
        });
        db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
        {
            Id = 900,
            CatalogReleaseId = 500,
            SourceEntryId = 1,
            ProviderClaimKey = "1",
            AssertedTitleId = 1
        });
        await db.SaveChangesAsync();

        var provider = new MaterializationDataProvider(db);
        var results = await provider.GetCandidatesAsync(new LibraryConfiguration { AllowedPlatformIds = [1] });

        var candidates = results.Single(title => title.TitleId == 1).Candidates;
        candidates.Count.ShouldBe(2);
        candidates.Single(candidate => candidate.DatGameId == 1).CatalogReleaseId.ShouldBe(500);
        candidates.Single(candidate => candidate.DatGameId == 2).CatalogReleaseId.ShouldBeNull();
    }

    [Fact]
    public async Task GetCandidatesAsync_TitleSelectionModeIncludeOnly_ReturnsOnlyIncludedTitles()
    {
        using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Titles.Add(new TitleEntity
        {
            Id = 3,
            PlatformId = 1,
            Name = "Whitelist Game",
            NormalizedName = "whitelist game",
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 2,
            CatalogSourceId = 1,
            EntryKey = "Whitelist Game",
            Name = "Whitelist Game",
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatGames.Add(new DatGameEntity
        {
            Id = 2,
            DatFileId = 1,
            SourceEntryId = 2,
            Name = "Whitelist Game",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 2,
            TitleId = 3,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        await db.SaveChangesAsync();

        var provider = new MaterializationDataProvider(db);
        var results = await provider.GetCandidatesAsync(new LibraryConfiguration
        {
            TitleSelectionMode = LibraryTitleSelectionMode.IncludeOnly,
            AllowedPlatformIds = [2],
            IncludeTitleIds = [3]
        });

        results.Single().TitleId.ShouldBe(3);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void Seed(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Platforms.AddRange(
            new PlatformEntity
            {
                Id = 1,
                Name = "Nintendo Entertainment System",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo Entertainment System", BaseCompactLabel = "Nintendo Entertainment System", CanonicalKey = "nes", ShortName = "nes",
                Manufacturer = "Nintendo",
                CreatedAt = now,
                CreatedByUserId = userId
            },
            new PlatformEntity
            {
                Id = 2,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
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
            OriginalFilename = "mega-game-a.nes",
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

        db.Titles.AddRange(
            new TitleEntity
            {
                Id = 1,
                PlatformId = 1,
                Name = "Mega Game",
                NormalizedName = "mega game",
                Genre = "Action",
                EnrichmentStatus = "None",
                CreatedAt = now,
                CreatedByUserId = userId
            },
            new TitleEntity
            {
                Id = 2,
                PlatformId = 2,
                Name = "Other Platform Game",
                NormalizedName = "other platform game",
                EnrichmentStatus = "None",
                CreatedAt = now,
                CreatedByUserId = userId
            });

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1,
            CatalogSourceId = 1,
            EntryKey = "Mega Game",
            Name = "Mega Game",
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.DatGames.Add(new DatGameEntity
        {
            Id = 1,
            DatFileId = 1,
            SourceEntryId = 1,
            Name = "Mega Game",
            Revision = "Rev 1",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 1,
            TitleId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.TitleContentRatings.Add(new TitleContentRatingEntity
        {
            TitleId = 1,
            Board = (int)RatingBoard.Esrb,
            Code = "E",
            Designation = (int)RatingDesignation.Rated,
            MinimumAge = 0,
            SourceId = "igdb",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        db.Regions.AddRange(
            new RegionEntity { Id = 1, Name = "USA", SortOrder = 1, CreatedAt = now, CreatedByUserId = userId },
            new RegionEntity { Id = 2, Name = "Europe", SortOrder = 2, CreatedAt = now, CreatedByUserId = userId });

        db.GameLanguages.AddRange(
            new GameLanguageEntity
            {
                Id = 1,
                Name = "English",
                Code = "en",
                SortOrder = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            },
            new GameLanguageEntity
            {
                Id = 2,
                Name = "French",
                Code = "fr",
                SortOrder = 2,
                CreatedAt = now,
                CreatedByUserId = userId
            });

        db.DatGameRegions.AddRange(
            new DatGameRegionEntity { DatGameId = 1, RegionId = 1, CreatedAt = now, CreatedByUserId = userId },
            new DatGameRegionEntity { DatGameId = 1, RegionId = 2, CreatedAt = now, CreatedByUserId = userId });

        db.DatGameLanguages.AddRange(
            new DatGameLanguageEntity { DatGameId = 1, GameLanguageId = 1, CreatedAt = now, CreatedByUserId = userId },
            new DatGameLanguageEntity { DatGameId = 1, GameLanguageId = 2, CreatedAt = now, CreatedByUserId = userId });

        db.DatRoms.AddRange(
            new DatRomEntity
            {
                Id = 1,
                DatGameId = 1,
                Name = "mega-game-a.nes",
                Size = 1,
                RomFileId = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            },
            new DatRomEntity
            {
                Id = 2,
                DatGameId = 1,
                Name = "mega-game-b.nes",
                Size = 1,
                RomFileId = null,
                CreatedAt = now,
                CreatedByUserId = userId
            });

        db.SaveChanges();
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Romd.Domain.Hashing.Sha256.ByteLength];
        bytes[0] = firstByte;
        return Romd.Domain.Hashing.Sha256.FromBytes(bytes);
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

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; }

        public IReadOnlyList<string> ReaderCommands => _readerCommands;

        private readonly List<string> _readerCommands = [];

        public void Reset()
        {
            ReaderCount = 0;
            _readerCommands.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCount++;
            _readerCommands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCount++;
            _readerCommands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
