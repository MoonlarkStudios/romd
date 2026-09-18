using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Tests.Persistence;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence.Search;

/// <summary>
///     Deterministic catalog for the #173 golden search corpus: three platforms, one DAT per
///     platform, 26 titles mirrored as DAT games (ids 101..126) plus one BIOS game (199), a
///     consumer library that owns every title except <c>Ōkami</c> and <c>EarthBound</c>, tracked
///     titles, and release rows that make exactly one title complete and one partial. Names are
///     chosen to exercise case, diacritics, compatibility forms, punctuation, Japanese text,
///     duplicate names, and description matches. The database is a copy of the migrated
///     PostgreSQL template, and the context carries the search-document interceptor.
/// </summary>
public sealed class SearchGoldenFixture : IAsyncLifetime
{
    public const int NesPlatformId = 1;
    public const int SnesPlatformId = 2;
    public const int GbPlatformId = 3;
    public const int UsaRegionId = 1;
    public const int EuropeRegionId = 2;
    public const int JapanRegionId = 3;
    public const int DatGameIdOffset = 100;
    public const int BiosDatGameId = 199;
    public static readonly Guid UserId = Guid.Parse("6f0f0f4a-1a5f-4d8a-9d8e-0a1b2c3d4e5f");

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    private static readonly TitleSeed[] Titles =
    [
        new(1, NesPlatformId, "Super Mario Bros.", "Platformer", 8.5, "1985", [UsaRegionId, JapanRegionId],
            Description: "Jump over Goombas in the Mushroom Kingdom"),
        new(2, NesPlatformId, "Super Mario Bros. 2", "Platformer", 7.0, "1988", [UsaRegionId]),
        new(3, NesPlatformId, "Super Mario Bros. 3", "Platformer", 9.0, "1990", [UsaRegionId, EuropeRegionId]),
        new(4, SnesPlatformId, "Super Mario World", "Platformer", 9.2, "1990", [UsaRegionId]),
        new(5, NesPlatformId, "Mario Bros.", "Arcade", 6.5, "1983", [JapanRegionId]),
        new(6, NesPlatformId, "Dr. Mario", "Puzzle", 7.5, "1990", [UsaRegionId]),
        new(7, GbPlatformId, "Dr. Mario", "Puzzle", 7.5, "1990", [UsaRegionId]),
        new(8, GbPlatformId, "Mario's Picross", "Puzzle", 7.8, "1995", [EuropeRegionId]),
        new(9, NesPlatformId, "Mega Man 2", "Action", 9.1, "1988", [UsaRegionId], Manufacturer: "Capcom"),
        new(10, SnesPlatformId, "Mega-Man X", "Action", 8.9, "1993", [UsaRegionId], Manufacturer: "Capcom"),
        new(11, GbPlatformId, "Pokémon Red", "RPG", 8.8, "1996", [JapanRegionId],
            Description: "Catch them all in Kanto"),
        new(12, GbPlatformId, "Pokemon Blue", "RPG", 8.8, "1996", [UsaRegionId]),
        new(13, SnesPlatformId, "Ōkami", "Action", null, "1995", [JapanRegionId], Manufacturer: "Capcom",
            Owned: false),
        new(14, SnesPlatformId, "Über Racer", "Racing", 5.0, "1994", [EuropeRegionId]),
        new(15, NesPlatformId, "The Legend of Zelda", "Adventure", 9.0, "1986", [UsaRegionId]),
        new(16, NesPlatformId, "Zelda II: The Adventure of Link", "Adventure", 7.9, "1987", [UsaRegionId]),
        new(17, SnesPlatformId, "Legend of Zelda, The: A Link to the Past", "Adventure", 9.6, "1991",
            [EuropeRegionId]),
        new(18, NesPlatformId, "ドラゴンクエスト", "RPG", 8.0, "1986", [JapanRegionId], Manufacturer: "Enix"),
        new(19, SnesPlatformId, "Ｆ－ＺＥＲＯ", "Racing", 8.2, "1990", [JapanRegionId]),
        new(20, SnesPlatformId, "F-Zero X", "Racing", 8.4, "1998", [UsaRegionId]),
        new(21, NesPlatformId, "Castlevania", "Action", 8.3, "1986", [UsaRegionId], Manufacturer: "Konami",
            Description: "Vampire hunter Simon Belmont"),
        new(22, NesPlatformId, "Contra", "Action", 8.1, "1987", [UsaRegionId, JapanRegionId],
            Manufacturer: "Konami", Description: "Run and gun"),
        new(23, GbPlatformId, "Kirby's Dream Land", "Platformer", 7.7, "1992", [UsaRegionId]),
        new(24, GbPlatformId, "Tetris", "Puzzle", 9.5, "1989", [UsaRegionId]),
        new(25, NesPlatformId, "Tetris", "Puzzle", 8.0, "1989", [JapanRegionId]),
        new(26, SnesPlatformId, "EarthBound", "RPG", 9.3, "1994", [UsaRegionId], EnrichmentStatus: "Enriched",
            Owned: false)
    ];

    private static readonly int[] TrackedTitleIds = [1, 15, 24];

    private PostgreSqlTestDatabase? _database;

    public int LibraryId { get; private set; }

    public async Task InitializeAsync()
    {
        _database = PostgreSqlTestDatabase.Create();

        await using var db = CreateContext();
        await SeedAsync(db);
    }

    public Task DisposeAsync()
    {
        _database?.Dispose();
        return Task.CompletedTask;
    }

    public RomdDbContext CreateContext() =>
        (_database ?? throw new InvalidOperationException("Fixture not initialized.")).CreateContext();

    private async Task SeedAsync(RomdDbContext db)
    {
        db.Platforms.AddRange(
            Platform(NesPlatformId, "Nintendo Entertainment System", "nes"),
            Platform(SnesPlatformId, "Super Nintendo Entertainment System", "snes"),
            Platform(GbPlatformId, "Game Boy", "gb"));
        db.Regions.AddRange(
            Region(UsaRegionId, "USA"),
            Region(EuropeRegionId, "Europe"),
            Region(JapanRegionId, "Japan"));
        foreach (int platformId in new[] { NesPlatformId, SnesPlatformId, GbPlatformId })
        {
            db.CatalogSources.Add(new CatalogSourceEntity
            {
                Id = platformId,
                Kind = "Dat",
                Status = "Active",
                CreatedAt = Now
            });
            db.DatSources.Add(new DatSourceEntity { Id = platformId, CatalogSourceId = platformId, CreatedAt = Now });
            db.Files.Add(new FileEntityPersistence
            {
                Id = platformId,
                Sha256 = Sha256.FromBytes(Enumerable.Repeat((byte)platformId, 32).ToArray()),
                Size = 1,
                SizeOnDisk = 1,
                CreatedAt = Now
            });
            db.DatFiles.Add(new DatFileEntity
            {
                Id = platformId,
                DatSourceId = platformId,
                Name = $"Golden DAT {platformId}",
                Description = $"Golden DAT {platformId}",
                Type = DatType.NoIntro.ToString(),
                PlatformId = platformId,
                OriginalFilename = $"golden-{platformId}.dat",
                FileId = platformId,
                CreatedAt = Now
            });
        }

        await db.SaveChangesAsync();

        foreach (var title in Titles)
        {
            db.Titles.Add(new TitleEntity
            {
                Id = title.Id,
                PlatformId = title.PlatformId,
                Name = title.Name,
                NormalizedName = title.Name.ToLowerInvariant(),
                Description = title.Description,
                Genre = title.Genre,
                Rating = title.Rating,
                EnrichmentStatus = title.EnrichmentStatus,
                CreatedAt = Now
            });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = title.DatGameId,
                CatalogSourceId = title.PlatformId,
                EntryKey = title.Name,
                Name = title.Name,
                PlatformId = title.PlatformId,
                CreatedAt = Now
            });
            db.DatGames.Add(new DatGameEntity
            {
                Id = title.DatGameId,
                DatFileId = title.PlatformId,
                SourceEntryId = title.DatGameId,
                Name = title.Name,
                Description = title.Description,
                Year = title.Year,
                Manufacturer = title.Manufacturer,
                CreatedAt = Now
            });
            db.DatGameRegions.AddRange(title.RegionIds.Select(regionId => new DatGameRegionEntity
            {
                DatGameId = title.DatGameId,
                RegionId = regionId,
                CreatedAt = Now
            }));
        }

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = BiosDatGameId,
            CatalogSourceId = GbPlatformId,
            EntryKey = "[BIOS] Game Boy Boot ROM",
            Name = "[BIOS] Game Boy Boot ROM",
            PlatformId = GbPlatformId,
            CreatedAt = Now
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = BiosDatGameId,
            DatFileId = GbPlatformId,
            SourceEntryId = BiosDatGameId,
            Name = "[BIOS] Game Boy Boot ROM",
            Description = "Boot ROM",
            Manufacturer = "Nintendo",
            IsBios = true,
            CreatedAt = Now
        });
        db.TrackedTitles.AddRange(TrackedTitleIds.Select(titleId => new TrackedTitleEntity
        {
            TitleId = titleId,
            CreatedAt = Now,
            UpdatedAt = Now
        }));

        await db.SaveChangesAsync();

        var library = LibraryEntity.FromDomain(Library.CreateNew("Golden Library", new LibraryConfiguration()));
        library.NeedsMaterialization = false;
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        LibraryId = library.Id;

        db.Users.Add(new RomdUser
        {
            Id = UserId,
            UserName = "golden-user",
            NormalizedUserName = "GOLDEN-USER",
            Email = "golden@example.test",
            NormalizedEmail = "GOLDEN@EXAMPLE.TEST",
            LibraryId = library.Id,
            CreatedAt = Now
        });
        db.MaterializedLibraryTitles.AddRange(Titles
            .Where(title => title.Owned)
            .Select(title => new MaterializedLibraryTitleEntity
            {
                LibraryId = library.Id,
                TitleId = title.Id,
                PlatformId = title.PlatformId,
                Genre = title.Genre,
                IsVisible = true,
                IsOwned = true,
                IsPlayable = true,
                EligibleReleaseCount = 1,
                PlayableReleaseCount = 1,
                ExposedReleaseCount = 1,
                Availability = LibraryTitleAvailability.Playable.ToString()
            }));
        db.MaterializedLibraryReleases.AddRange(
            Release(library.Id, titleId: 1, datGameId: 101, isComplete: true),
            Release(library.Id, titleId: 1, datGameId: 102, isComplete: true),
            Release(library.Id, titleId: 2, datGameId: 103, isComplete: true),
            Release(library.Id, titleId: 2, datGameId: 104, isComplete: false));

        await db.SaveChangesAsync();
    }

    private static PlatformEntity Platform(int id, string name, string shortName) =>
        new() { Id = id, Name = name, Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = name, BaseCompactLabel = name, CanonicalKey = shortName, ShortName = shortName, CreatedAt = Now };

    private static RegionEntity Region(int id, string name) =>
        new() { Id = id, Name = name, SortOrder = id, CreatedAt = Now };

    private static MaterializedLibraryReleaseEntity Release(
        int libraryId,
        int titleId,
        int datGameId,
        bool isComplete) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            DatGameId = datGameId,
            DatFileId = NesPlatformId,
            PlatformId = NesPlatformId,
            IsEligible = true,
            IsComplete = isComplete,
            IsOwned = true,
            IsPlayable = isComplete,
            IsExposed = true,
            ExposureReason = "Golden"
        };

    private sealed record TitleSeed(
        int Id,
        int PlatformId,
        string Name,
        string Genre,
        double? Rating,
        string Year,
        int[] RegionIds,
        string Manufacturer = "Nintendo",
        string? Description = null,
        string EnrichmentStatus = "None",
        bool Owned = true)
    {
        public int DatGameId => DatGameIdOffset + Id;
    }
}
