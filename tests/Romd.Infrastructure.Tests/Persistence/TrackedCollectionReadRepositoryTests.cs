using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.TrackedCollection;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TrackedCollectionReadRepositoryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly CommandCounterInterceptor _commands = new();
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TrackedCollectionReadRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(_commands)
            .Options;

        using var db = CreateDb();
        SeedPlatform(db, 1, "Super Nintendo");
        db.SaveChanges();
        _commands.Reset();
    }

    [Fact]
    public async Task ListAsync_TrackedScope_DerivesSatisfiedAndMissingAndExcludesUntracked()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedTitle(db, 2, tracked: true);
        SeedTitle(db, 3, tracked: false);
        SeedRelease(db, 11, 1, "Owned", "USA", "Rev 1", (0x11, null));
        SeedRelease(db, 22, 2, "Missing", "USA", "Rev 1", (0x22, null));
        SeedRelease(db, 33, 3, "Untracked owned", "USA", "Rev 1", (0x33, null));
        SeedOwnedRom(db, 0x11);
        SeedOwnedRom(db, 0x33);
        await db.SaveChangesAsync();
        var repository = CreateRepository(db);

        var satisfied = await repository.ListAsync(TrackedCollectionView.Satisfied);
        var missing = await repository.ListAsync(TrackedCollectionView.Missing);

        satisfied.Select(title => title.TitleId).ShouldBe([1]);
        missing.Select(title => title.TitleId).ShouldBe([2]);
        satisfied.ShouldHaveSingleItem().SatisfiedAt.ShouldNotBeNull();
        missing.ShouldHaveSingleItem().SatisfiedAt.ShouldBeNull();
        satisfied.Concat(missing).ShouldNotContain(title => title.TitleId == 3);
    }

    [Fact]
    public async Task ListAsync_SatisfiedAtUsesLatestDerivedInputAndOrdersNewestFirstInOneCommand()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedTitle(db, 2, tracked: true);
        SeedTitle(db, 3, tracked: true);
        SeedTitle(db, 4, tracked: true);
        SeedRelease(db, 11, 1, "Latest ROM", "USA", null, (0x11, null));
        SeedRelease(db, 22, 2, "Latest intent", "USA", null, (0x22, null));
        SeedRelease(db, 33, 3, "Latest release", "USA", null, (0x33, null));
        SeedRelease(db, 44, 4, "Missing", "USA", null, (0x44, null));
        SeedOwnedRom(db, 0x11);
        SeedOwnedRom(db, 0x22);
        SeedOwnedRom(db, 0x33);

        db.TrackedTitles.Local.Single(title => title.TitleId == 1).UpdatedAt = baseline.AddDays(1);
        db.CatalogReleases.Local.Single(release => release.Id == 11).CreatedAt = baseline.AddDays(2);
        db.RomFiles.Local.Single(rom => rom.Id == 0x11).CreatedAt = baseline.AddDays(3);

        db.CatalogReleases.Local.Single(release => release.Id == 22).CreatedAt = baseline.AddDays(4);
        db.RomFiles.Local.Single(rom => rom.Id == 0x22).CreatedAt = baseline.AddDays(5);
        db.TrackedTitles.Local.Single(title => title.TitleId == 2).UpdatedAt = baseline.AddDays(6);

        db.TrackedTitles.Local.Single(title => title.TitleId == 3).UpdatedAt = baseline.AddDays(7);
        db.RomFiles.Local.Single(rom => rom.Id == 0x33).CreatedAt = baseline.AddDays(8);
        db.CatalogReleases.Local.Single(release => release.Id == 33).CreatedAt = baseline.AddDays(9);

        await db.SaveChangesAsync();
        _commands.Reset();

        var repository = CreateRepository(db);
        var satisfied = await repository.ListAsync(TrackedCollectionView.Satisfied);

        satisfied.Select(title => title.TitleId).ShouldBe([3, 2, 1]);
        satisfied.Select(title => title.SatisfiedAt).ShouldBe([
            baseline.AddDays(9),
            baseline.AddDays(6),
            baseline.AddDays(3)
        ]);

        _commands.ReaderCount.ShouldBe(1);
        string sql = _commands.CommandTexts.ShouldHaveSingleItem();
        sql.ShouldNotContain(" IN (");
        sql.ShouldContain("\"RomFiles\"");
        sql.ShouldContain("ORDER BY");
        sql.ShouldContain("LIMIT 1");

        _commands.Reset();
        var missing = await repository.ListAsync(TrackedCollectionView.Missing);

        _commands.ReaderCount.ShouldBe(1);
        missing.ShouldHaveSingleItem().SatisfiedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_OwnedBadDumpAndAvailableGoodDump_IsSatisfiedUpgrade()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedRelease(db, 11, 1, "Bad dump", "USA", "Rev 1", (0x11, "BaDdUmP"));
        SeedRelease(db, 12, 1, "Good dump", "Europe", "Rev 1", (0x12, "VeRiFiEd"));
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var upgrade = (await CreateRepository(db).ListAsync(TrackedCollectionView.Upgrades)).ShouldHaveSingleItem();

        upgrade.IsSatisfied.ShouldBeTrue();
        upgrade.HasUpgrade.ShouldBeTrue();
        upgrade.OwnedRelease!.CatalogReleaseId.ShouldBe(11);
        upgrade.DesiredRelease!.CatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task ListAsync_OwnedNonPreferredRegionAndAvailablePreferredRegion_IsUpgrade()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedRelease(db, 11, 1, "Europe", "Europe", "Rev 2", (0x11, null));
        SeedRelease(db, 12, 1, "USA", "USA", "Rev 1", (0x12, null));
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var upgrade = (await CreateRepository(db).ListAsync(TrackedCollectionView.Upgrades)).ShouldHaveSingleItem();

        upgrade.OwnedRelease!.CatalogReleaseId.ShouldBe(11);
        upgrade.DesiredRelease!.CatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task ListAsync_PinOverridesGlobalPreference()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true, pinnedReleaseId: 12);
        SeedRelease(db, 11, 1, "Preferred owned", "USA", "Rev 2", (0x11, null));
        SeedRelease(db, 12, 1, "Pinned missing", "Europe", "Rev 1", (0x12, "BaDdUmP"));
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var upgrade = (await CreateRepository(db).ListAsync(TrackedCollectionView.Upgrades)).ShouldHaveSingleItem();

        upgrade.IsPinned.ShouldBeTrue();
        upgrade.IsSatisfied.ShouldBeTrue();
        upgrade.OwnedRelease!.CatalogReleaseId.ShouldBe(11);
        upgrade.DesiredRelease!.CatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task ListAsync_OwnedPinnedInferiorRelease_IsDesiredAndOwnedWithoutUpgrade()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true, pinnedReleaseId: 12);
        SeedRelease(db, 11, 1, "Verified USA", "USA", "Rev 2", (0x11, "verified"));
        SeedRelease(db, 12, 1, "Pinned baddump", "Europe", "Rev 1", (0x12, "baddump"));
        SeedOwnedRom(db, 0x11);
        SeedOwnedRom(db, 0x12);
        await db.SaveChangesAsync();
        var repository = CreateRepository(db);

        var satisfied = (await repository.ListAsync(TrackedCollectionView.Satisfied)).ShouldHaveSingleItem();
        var upgrades = await repository.ListAsync(TrackedCollectionView.Upgrades);

        satisfied.IsPinned.ShouldBeTrue();
        satisfied.HasUpgrade.ShouldBeFalse();
        satisfied.DesiredRelease!.CatalogReleaseId.ShouldBe(12);
        satisfied.OwnedRelease!.CatalogReleaseId.ShouldBe(12);
        upgrades.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_MultiFileAndNoDumpRequirements_ApplyReleaseCompleteness()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedTitle(db, 2, tracked: true);
        SeedTitle(db, 3, tracked: true);
        SeedTitle(db, 4, tracked: true);
        SeedRelease(db, 11, 1, "Incomplete multi-file", "USA", null, (0x11, null), (0x12, null));
        SeedRelease(db, 22, 2, "Owned plus nodump", "USA", null, (0x21, null), (0x22, "NoDump"));
        SeedRelease(db, 33, 3, "Nodump only", "USA", null, (0x31, "NODUMP"));
        SeedRelease(db, 44, 4, "No requirements", "USA", null);
        SeedOwnedRom(db, 0x11);
        SeedOwnedRom(db, 0x21);
        await db.SaveChangesAsync();
        var repository = CreateRepository(db);

        var missing = await repository.ListAsync(TrackedCollectionView.Missing);
        var satisfied = await repository.ListAsync(TrackedCollectionView.Satisfied);

        missing.Select(title => title.TitleId).ShouldBe([1, 3, 4]);
        satisfied.Select(title => title.TitleId).ShouldBe([2]);
    }

    [Fact]
    public async Task ListAsync_NullShaRequiredFile_PreventsCompleteness()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedRelease(db, 11, 1, "Null SHA-1 requirement", "USA", null, (0x11, null));
        SeedNullShaRequiredFile(db, releaseId: 11, fileId: 119);
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var missing = await CreateRepository(db).ListAsync(TrackedCollectionView.Missing);

        missing.ShouldHaveSingleItem().TitleId.ShouldBe(1);
    }

    [Fact]
    public async Task ListAsync_DuplicateRequiredHashAcrossReleases_DoesNotInflateCompleteness()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true, pinnedReleaseId: 11);
        SeedRelease(
            db,
            11,
            1,
            "Pinned multi-file",
            "USA",
            null,
            (0x11, null),
            (0x11, null),
            (0x12, null));
        SeedRelease(db, 12, 1, "Complete shared hash", "Europe", null, (0x11, null));
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var upgrade = (await CreateRepository(db).ListAsync(TrackedCollectionView.Upgrades))
            .ShouldHaveSingleItem();

        upgrade.DesiredRelease!.CatalogReleaseId.ShouldBe(11);
        upgrade.OwnedRelease!.CatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task ListAsync_NewerParsedRevisionWinsDeterministicPreference()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedRelease(db, 11, 1, "Revision 1", "USA", "Rev 1.9", (0x11, null));
        SeedRelease(db, 12, 1, "Revision 2", "USA", "Rev 2.0", (0x12, null));
        SeedOwnedRom(db, 0x11);
        await db.SaveChangesAsync();

        var upgrade = (await CreateRepository(db).ListAsync(TrackedCollectionView.Upgrades)).ShouldHaveSingleItem();

        upgrade.OwnedRelease!.CatalogReleaseId.ShouldBe(11);
        upgrade.DesiredRelease!.CatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCompletionAndTrackedPerPlatform()
    {
        using var db = CreateDb();
        SeedPlatform(db, 2, "Nintendo 64");
        SeedTitle(db, 1, tracked: true);
        SeedTitle(db, 2, tracked: true);
        SeedTitle(db, 3, tracked: true, platformId: 2);
        SeedRelease(db, 11, 1, "Owned", "USA", null, (0x11, null));
        SeedRelease(db, 22, 2, "Missing", "USA", null, (0x22, null));
        SeedRelease(db, 33, 3, "Owned", "USA", null, (0x33, null));
        SeedOwnedRom(db, 0x11);
        SeedOwnedRom(db, 0x33);
        await db.SaveChangesAsync();

        var stats = await CreateRepository(db).GetStatsAsync();

        stats.TrackedTitleCount.ShouldBe(3);
        stats.SatisfiedTitleCount.ShouldBe(2);
        stats.MissingTitleCount.ShouldBe(1);
        stats.UpgradeTitleCount.ShouldBe(0);
        stats.CompletionPercent.ShouldBe(66.7m);
        stats.Platforms.Count.ShouldBe(2);
        var snes = stats.Platforms.Single(platform => platform.PlatformId == 1);
        snes.TrackedTitleCount.ShouldBe(2);
        snes.SatisfiedTitleCount.ShouldBe(1);
        snes.CompletionPercent.ShouldBe(50m);
    }

    [Fact]
    public async Task GetStatsAsync_QueryCountIsConstantAndDoesNotUseContainsExpansion()
    {
        using var db = CreateDb();
        SeedTitle(db, 1, tracked: true);
        SeedRelease(db, 11, 1, "One", "USA", null, (0x11, null));
        await db.SaveChangesAsync();
        var repository = CreateRepository(db);
        _commands.Reset();

        await repository.GetStatsAsync();

        _commands.ReaderCount.ShouldBe(1);
        string oneTitleSql = _commands.CommandTexts.ShouldHaveSingleItem();
        oneTitleSql.ShouldNotContain(" IN (");
        oneTitleSql.ShouldContain("COUNT");
        oneTitleSql.ShouldNotContain("LEFT JOIN romd.\"CatalogReleaseFiles\"");

        SeedTitle(db, 2, tracked: true);
        SeedTitle(db, 3, tracked: true);
        SeedRelease(db, 22, 2, "Two", "USA", null, (0x22, null));
        SeedRelease(db, 33, 3, "Three", "USA", null, (0x33, null));
        await db.SaveChangesAsync();
        _commands.Reset();

        await repository.GetStatsAsync();

        _commands.ReaderCount.ShouldBe(1);
        string threeTitleSql = _commands.CommandTexts.ShouldHaveSingleItem();
        threeTitleSql.ShouldNotContain(" IN (");
        threeTitleSql.ShouldContain("COUNT");
        threeTitleSql.ShouldNotContain("LEFT JOIN romd.\"CatalogReleaseFiles\"");
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static TrackedCollectionReadRepository CreateRepository(RomdDbContext db) =>
        new(db, Options.Create(new EnrichmentOptions
        {
            RegionPriority = ["USA", "Europe", "Japan"]
        }));

    private static void SeedPlatform(RomdDbContext db, int id, string name) =>
        db.Platforms.Add(new PlatformEntity
        {
            Id = id,
            Name = name,
            ShortName = $"platform-{id}",
            CreatedAt = DateTimeOffset.UtcNow
        });

    private static void SeedTitle(
        RomdDbContext db,
        int id,
        bool tracked,
        int platformId = 1,
        int? pinnedReleaseId = null)
    {
        var now = DateTimeOffset.UtcNow;
        db.Titles.Add(new TitleEntity
        {
            Id = id,
            PlatformId = platformId,
            Name = $"Title {id}",
            NormalizedName = $"title{id}",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = now
        });
        if (tracked)
        {
            db.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = id,
                PinnedCatalogReleaseId = pinnedReleaseId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    private static void SeedRelease(
        RomdDbContext db,
        int id,
        int titleId,
        string name,
        string? region,
        string? revision,
        params (byte Hash, string? Status)[] files)
    {
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = id,
            PlatformId = 1,
            CatalogTitleId = titleId,
            Fingerprint = $"release-{id}",
            Name = name,
            Region = region,
            Revision = revision,
            FileCount = files.Length,
            SizeBytes = files.Length,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        int fileIndex = 0;
        foreach (var (hash, status) in files)
        {
            fileIndex++;
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                Id = (id * 10) + fileIndex,
                CatalogReleaseId = id,
                FileFingerprint = $"sha1-{hash:x2}-{fileIndex}",
                Name = $"file-{hash:x2}.rom",
                Size = 1,
                Sha1 = NewSha1(hash),
                Status = status
            });
        }
    }

    private static void SeedNullShaRequiredFile(RomdDbContext db, int releaseId, int fileId) =>
        db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
        {
            Id = fileId,
            CatalogReleaseId = releaseId,
            FileFingerprint = $"null-sha1-{fileId}",
            Name = $"null-sha1-{fileId}.rom",
            Size = 1
        });

    private static void SeedOwnedRom(RomdDbContext db, byte hash)
    {
        int id = hash;
        db.Files.Add(new FileEntityPersistence
        {
            Id = id,
            Sha256 = NewSha256(hash),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.RomFiles.Add(new RomFileEntity
        {
            Id = id,
            FileId = id,
            OriginalFilename = $"owned-{hash:x2}.rom",
            Sha1 = NewSha1(hash),
            Md5 = NewMd5(hash),
            Crc32 = NewCrc32(hash),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromBytes(bytes);
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private static Md5 NewMd5(byte firstByte)
    {
        var bytes = new byte[Md5.ByteLength];
        bytes[0] = firstByte;
        return Md5.FromBytes(bytes);
    }

    private static Crc32 NewCrc32(byte firstByte)
    {
        var bytes = new byte[Crc32.ByteLength];
        bytes[0] = firstByte;
        return Crc32.FromBytes(bytes);
    }

    private sealed class CommandCounterInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; }
        public List<string> CommandTexts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCount++;
            CommandTexts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public void Reset()
        {
            ReaderCount = 0;
            CommandTexts.Clear();
        }
    }
}
