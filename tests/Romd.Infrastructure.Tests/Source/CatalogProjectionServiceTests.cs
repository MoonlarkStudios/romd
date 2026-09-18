using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Infrastructure;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class CatalogProjectionServiceTests : IDisposable
{
    private const int PlatformId = 1;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public CatalogProjectionServiceTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebuildPlatformAsync_OverlappingAttempts_SerializeAndNewestSuccessStaysClean(bool failFirst)
    {
        using (var seed = CreateDb())
        {
            SeedTitle(seed, 10);
            SeedMappedGame(seed, 1, 1, 1, 10, NewSha1(0x11));
            await seed.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var firstDb = CreateDb(new FailCatalogReleaseSaveInterceptor("first attempt failed") { Enabled = failFirst });
        var gate = new GatedSnapshotReader(new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(firstDb)]));
        var firstService = new CatalogProjectionService(firstDb, gate, Substitute.For<ITitlePayloadAvailabilityProjection>(),
            TimeProvider.System, Substitute.For<ILogger<CatalogProjectionService>>());
        var first = firstService.RebuildPlatformAsync(PlatformId, timeout.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(timeout.Token);
            // Prove the serialization boundary while the first source read is held,
            // without relying on a sleep or a fast/slow scheduling assumption.
            using (var probe = CreateDb())
            {
                var blocked = await Should.ThrowAsync<PostgresException>(() => probe.Database.ExecuteSqlRawAsync(
                    "SELECT \"Id\" FROM romd.\"Platforms\" WHERE \"Id\" = 1 FOR UPDATE NOWAIT", timeout.Token));
                blocked.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable);
            }
            var attempt = new PlatformUpdateAttemptInterceptor();
            using var secondDb = CreateDb(attempt);
            var second = CreateService(secondDb).RebuildPlatformAsync(PlatformId, timeout.Token);
            await attempt.Entered.Task.WaitAsync(timeout.Token);
            gate.Release.TrySetResult();
            (await first).ShouldBe(!failFirst);
            (await second).ShouldBeTrue();
        }
        finally { gate.Release.TrySetResult(); }
        using var verify = CreateDb();
        (await verify.CatalogReleases.CountAsync()).ShouldBe(1);
        (await verify.CatalogReleaseFiles.CountAsync()).ShouldBe(1);
        (await verify.CatalogReleaseSources.CountAsync()).ShouldBe(1);
        var platform = await verify.Platforms.SingleAsync();
        platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        platform.CatalogRebuildError.ShouldBeNull();
    }

    private sealed class GatedSnapshotReader(ICatalogSourceSnapshotReader inner) : ICatalogSourceSnapshotReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CatalogSourceSnapshot> ReadPlatformAsync(int platformId, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await inner.ReadPlatformAsync(platformId, ct);
        }
    }
    private sealed class PlatformUpdateAttemptInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("UPDATE romd.\"Platforms\"", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task RebuildPlatformAsync_SingleMappedGame_CreatesReleaseFilesAndSources()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
        await db.SaveChangesAsync();

        bool rebuilt = await CreateService(db).RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.PlatformId.ShouldBe(PlatformId);
        release.CatalogTitleId.ShouldBe(10);
        release.Fingerprint.ShouldBe($"sha1-v1:{NewSha1(0x11)}");
        release.PrimarySha1.ShouldBe(NewSha1(0x11));
        release.SourceCount.ShouldBe(1);
        release.HasTitleConflict.ShouldBeFalse();

        (await db.CatalogReleaseFiles.AsNoTracking().CountAsync()).ShouldBe(1);
        var source = await db.CatalogReleaseSources.AsNoTracking().SingleAsync();
        source.SourceEntryId.ShouldBe(1);
        source.ProviderClaimKey.ShouldBe("1");
        source.AssertedTitleId.ShouldBe(10);
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(1);

        var title = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == 10);
        title.CatalogState.ShouldBe(TitleCatalogState.Active);
        var platform = await db.Platforms.AsNoTracking().SingleAsync(p => p.Id == PlatformId);
        platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
    }

    [Fact]
    public async Task LayeredCurationComposition_RegisteredTwice_RebuildsNonEmptyCatalogOnce()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x12));
            await seedDb.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdTestingPersistence(_connection.ConnectionString);
        services.AddRomdAdminCurationServices();
        services.AddRomdWorkerCurationServices();
        using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetServices<ICatalogSourceSnapshotProvider>().Count().ShouldBe(1);
            var projection = scope.ServiceProvider.GetRequiredService<ICatalogProjectionService>();
            (await projection.RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
        }

        using var assertDb = CreateDb();
        (await assertDb.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(1);
        (await assertDb.CatalogReleaseSources.AsNoTracking().CountAsync()).ShouldBe(1);
        (await assertDb.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RebuildPlatformAsync_PersistsReleaseSizeAndFileCount()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
        await db.SaveChangesAsync();

        bool rebuilt = await CreateService(db).RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.FileCount.ShouldBe(1);
        release.SizeBytes.ShouldBe(1024);

        // The persisted total mirrors the per-release SUM(Size) it replaced.
        long fileSizeSum = await db.CatalogReleaseFiles.AsNoTracking()
            .Where(file => file.CatalogReleaseId == release.Id)
            .SumAsync(file => file.Size);
        release.SizeBytes.ShouldBe(fileSizeSum);
    }

    [Fact]
    public async Task RebuildPlatformAsync_SameShaInTwoDats_ProducesOneReleaseWithTwoSources()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var sha1 = NewSha1(0x22);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1);
        SeedMappedGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 10, sha1);
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);

        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.SourceCount.ShouldBe(2);
        (await db.CatalogReleaseSources.AsNoTracking().CountAsync()).ShouldBe(2);
        (await db.CatalogReleaseFiles.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RebuildPlatformAsync_DuplicateFileFingerprint_PreservesClaimThenProvenanceOrdering()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var sha1 = NewSha1(0x22);
        SeedMappedGame(
            db,
            datFileId: 1,
            gameId: 1,
            romId: 20,
            titleId: 10,
            sha1,
            romName: "claim-order-winner.sfc");
        SeedMappedGame(
            db,
            datFileId: 2,
            gameId: 2,
            romId: 10,
            titleId: 10,
            sha1,
            romName: "lower-provenance-id.sfc");
        await db.SaveChangesAsync();

        (await CreateService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        var file = await db.CatalogReleaseFiles.AsNoTracking().SingleAsync();
        file.Name.ShouldBe("claim-order-winner.sfc");
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task RebuildPlatformAsync_DormantSourceSharesRelease_ProjectsOnlyActiveSourceFacts()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var sha1 = NewSha1(0x23);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1);
        SeedMappedGame(
            db,
            datFileId: 2,
            gameId: 2,
            romId: 2,
            titleId: 10,
            sha1,
            sourceStatus: CatalogSourceStatus.Disabled);
        await db.SaveChangesAsync();

        bool rebuilt = await CreateService(db).RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.SourceCount.ShouldBe(1);
        var source = await db.CatalogReleaseSources.AsNoTracking().SingleAsync();
        source.SourceEntryId.ShouldBe(1);
        source.AssertedTitleId.ShouldBe(10);
        var fileSource = await db.CatalogReleaseFileSources.AsNoTracking().SingleAsync();
        fileSource.ProviderRequirementKey.ShouldBe("1");
        fileSource.RequirementKind.ShouldBe(nameof(CatalogSourceRequirementKind.Rom));
    }

    [Fact]
    public async Task RebuildPlatformAsync_BiosClaimIsLinked_DoesNotProjectRelease()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(
            db,
            datFileId: 1,
            gameId: 1,
            romId: 1,
            titleId: 10,
            NewSha1(0x24),
            isBios: true);
        await db.SaveChangesAsync();

        bool rebuilt = await CreateService(db).RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(0);
        (await db.CatalogReleaseSources.AsNoTracking().CountAsync()).ShouldBe(0);
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RebuildPlatformAsync_ReplaceWithSameSha_KeepsStableReleaseId()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var sha1 = NewSha1(0x33);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1);
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);
        int originalReleaseId = (await db.CatalogReleases.AsNoTracking().SingleAsync()).Id;

        // Replacement DAT ingested before the old one is deleted (the real ReplaceDat order).
        SeedMappedGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 10, sha1);
        await db.SaveChangesAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);
        (await db.CatalogReleases.AsNoTracking().SingleAsync()).Id.ShouldBe(originalReleaseId);

        // Old DAT's game removed; the release stays, now asserted only by the replacement.
        await db.DatGames.Where(g => g.Id == 1).ExecuteDeleteAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);

        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.Id.ShouldBe(originalReleaseId);
        release.SourceCount.ShouldBe(1);
        (await db.CatalogReleaseSources.AsNoTracking().SingleAsync()).SourceEntryId.ShouldBe(2);
    }

    [Fact]
    public async Task RebuildPlatformAsync_StableReleaseRepointedToDifferentTitle_ClearsInvalidPin()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10, isTracked: true);
        SeedTitle(db, titleId: 20);
        var sha1 = NewSha1(0x34);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1);
        await db.SaveChangesAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);
        int releaseId = await db.CatalogReleases.AsNoTracking().Select(release => release.Id).SingleAsync();
        await db.TrackedTitles
            .Where(tracked => tracked.TitleId == 10)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(tracked => tracked.PinnedCatalogReleaseId, releaseId)
                .SetProperty(tracked => tracked.UpdatedAt, DateTimeOffset.UnixEpoch));
        await db.TitleSourceLinks
            .Where(link => link.SourceEntryId == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.TitleId, 20));
        var rebuiltAt = new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero);

        bool rebuilt = await CreateService(db, new ManualTimeProvider(rebuiltAt))
            .RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.Id.ShouldBe(releaseId);
        release.CatalogTitleId.ShouldBe(20);
        var tracked = await db.TrackedTitles.AsNoTracking().SingleAsync(row => row.TitleId == 10);
        tracked.PinnedCatalogReleaseId.ShouldBeNull();
        tracked.UpdatedAt.ShouldBe(rebuiltAt);
    }

    [Fact]
    public async Task RebuildPlatformAsync_SameFingerprintDifferentTitles_ResolvesDeterministicallyAndFlagsConflict()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedTitle(db, titleId: 20);
        var sha1 = NewSha1(0x44);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1);
        SeedMappedGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 20, sha1);
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);

        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.CatalogTitleId.ShouldBe(10); // min DatGameId (1) wins
        release.HasTitleConflict.ShouldBeTrue();
        var assertedTitles = await db.CatalogReleaseSources.AsNoTracking()
            .Select(s => s.AssertedTitleId)
            .ToListAsync();
        assertedTitles.ShouldBe([10, 20], ignoreOrder: true);
    }

    [Fact]
    public async Task RebuildPlatformAsync_DatReplacementChurn_RetainsTrackedIntentAcrossPruneAndReconnect()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10, isTracked: true);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x55));
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);
        (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(1);

        await db.DatGames.Where(g => g.Id == 1).ExecuteDeleteAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);

        (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(0);
        var title = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == 10);
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await db.TrackedTitles.AnyAsync(t => t.TitleId == 10)).ShouldBeTrue();

        SeedMappedGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 10, NewSha1(0x56));
        await db.SaveChangesAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);

        (await db.CatalogReleases.AsNoTracking().CountAsync()).ShouldBe(1);
        (await db.Titles.AsNoTracking().SingleAsync(t => t.Id == 10)).CatalogState
            .ShouldBe(TitleCatalogState.Active);
        (await db.TrackedTitles.AnyAsync(t => t.TitleId == 10)).ShouldBeTrue();
    }

    [Fact]
    public async Task RebuildPlatformAsync_NoShaGamesAcrossDats_FallbackFingerprintReconnects()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sha1: null, gameName: "Shared Game");
        SeedMappedGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 10, sha1: null, gameName: "Shared Game");
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);

        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.Fingerprint.ShouldStartWith("datgame-fallback-v1:");
        release.PrimarySha1.ShouldBeNull();
        // The two identical no-SHA games come from different DAT files; if DatFileId leaked into
        // the fallback hash they would NOT collapse. One release with two sources proves it does not.
        release.SourceCount.ShouldBe(2);
    }

    [Fact]
    public async Task RebuildPlatformAsync_DiskBasedGame_ProjectsDiskReleaseFile()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var diskSha1 = NewSha1(0x77);
        SeedMappedDiskGame(db, datFileId: 1, gameId: 1, diskId: 1, titleId: 10, diskSha1);
        await db.SaveChangesAsync();

        await CreateService(db).RebuildPlatformAsync(PlatformId);

        var release = await db.CatalogReleases.AsNoTracking().SingleAsync();
        release.Fingerprint.ShouldBe($"sha1-v1:{diskSha1}");
        var file = await db.CatalogReleaseFiles.AsNoTracking().SingleAsync();
        file.IsDisk.ShouldBeTrue();
        file.Sha1.ShouldBe(diskSha1);
        var fileSource = await db.CatalogReleaseFileSources.AsNoTracking().SingleAsync();
        fileSource.ProviderRequirementKey.ShouldBe("1");
        fileSource.RequirementKind.ShouldBe(nameof(CatalogSourceRequirementKind.Disk));
    }

    [Fact]
    public async Task CatalogReleaseFileSource_WithInvalidRequirementKind_IsRejectedByCheckConstraint()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
        await db.SaveChangesAsync();
        await CreateService(db).RebuildPlatformAsync(PlatformId);

        int fileId = await db.CatalogReleaseFiles.AsNoTracking().Select(file => file.Id).FirstAsync();

        await Should.ThrowAsync<PostgresException>(async () => await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO CatalogReleaseFileSources " +
            "(CatalogReleaseFileId, SourceEntryId, ProviderClaimKey, RequirementKind, ProviderRequirementKey) " +
            "VALUES ({0}, 1, '1', 'Unknown', '1')",
            fileId));
    }

    [Fact]
    public async Task RebuildPlatformAsync_ImportClaimsWithCollidingKeys_ProjectWithoutDatRows()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        // Provider-local key "1" deliberately collides with these DAT payload ids. The
        // Import entry scopes the key, so no DAT FK or numeric id-space coupling is possible.
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x01));
        var now = DateTimeOffset.UtcNow;
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 20,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Active),
            Name = "Path import",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 20,
            CatalogSourceId = 20,
            EntryKey = "import-entry",
            Name = "Imported game",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 20,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();

        var sha1 = NewSha1(0x7C);
        var requirement = new CatalogSourceRequirementSnapshot(
            "1", 1, CatalogSourceRequirementKind.Rom, "payload.rom", 1024,
            null, null, sha1, null);
        var snapshot = new CatalogSourceSnapshot([
            new CatalogSourceEntrySnapshot(
                20,
                CatalogSourceKind.Import,
                AssertedTitleId: 10,
                HasLocalPayload: true,
                [
                    new CatalogSourceClaimSnapshot(
                        "1", 0, 10, "Imported game", null, null, null, [requirement], [], []),
                    new CatalogSourceClaimSnapshot(
                        "old", 2, 9, "Imported game", null, null, null, [requirement], [], [])
                ])
        ]);
        var service = new CatalogProjectionService(
            db,
            new StaticSnapshotReader(snapshot),
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            TimeProvider.System,
            Substitute.For<ILogger<CatalogProjectionService>>());

        (await service.RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        var source = await db.CatalogReleaseSources.AsNoTracking().SingleAsync();
        source.SourceEntryId.ShouldBe(20);
        source.ProviderClaimKey.ShouldBe("1");
        var fileSources = await db.CatalogReleaseFileSources.AsNoTracking()
            .OrderBy(row => row.ProviderClaimKey)
            .ToListAsync();
        fileSources.Select(row => row.ProviderClaimKey).ShouldBe(["1", "old"]);
        fileSources.ShouldAllBe(row => row.ProviderRequirementKey == "1");
        fileSources.ShouldAllBe(row => row.SourceEntryId == 20);
        (await db.DatGames.AsNoTracking().CountAsync()).ShouldBe(1);
        (await db.DatRoms.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RebuildPlatformAsync_RequirementOrder_SelectsNumericProviderRepresentative()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var now = DateTimeOffset.UtcNow;
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 20,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Active),
            Name = "Import",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 20,
            CatalogSourceId = 20,
            EntryKey = "ordered",
            Name = "Ordered",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 20,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();

        var sha1 = NewSha1(0x7D);
        var snapshot = new CatalogSourceSnapshot([
            new CatalogSourceEntrySnapshot(20, CatalogSourceKind.Import, 10, true,
            [
                new CatalogSourceClaimSnapshot("claim", 0, 1, "Ordered", null, null, null,
                [
                    new CatalogSourceRequirementSnapshot(
                        "10", 10, CatalogSourceRequirementKind.Rom, "ten.rom", 10, null, null, sha1, null),
                    new CatalogSourceRequirementSnapshot(
                        "2", 2, CatalogSourceRequirementKind.Rom, "two.rom", 2, null, null, sha1, null)
                ], [], [])
            ])
        ]);
        var service = new CatalogProjectionService(
            db,
            new StaticSnapshotReader(snapshot),
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            TimeProvider.System,
            Substitute.For<ILogger<CatalogProjectionService>>());

        (await service.RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        var file = await db.CatalogReleaseFiles.AsNoTracking().SingleAsync();
        file.Name.ShouldBe("two.rom");
        file.Size.ShouldBe(2);
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task SourceEntryDelete_CascadesReleaseAndFileProvenance()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        SeedMappedGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x7E));
        await db.SaveChangesAsync();
        (await CreateService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        // Provider payload ownership is removed first; projection provenance deliberately
        // survives that provider-row cascade until the catalog-owned entry is deleted.
        await db.DatGames.Where(game => game.SourceEntryId == 1).ExecuteDeleteAsync();
        (await db.CatalogReleaseSources.AsNoTracking().CountAsync()).ShouldBe(1);
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(1);

        await db.SourceEntries.Where(entry => entry.Id == 1).ExecuteDeleteAsync();

        (await db.CatalogReleaseSources.AsNoTracking().CountAsync()).ShouldBe(0);
        (await db.CatalogReleaseFileSources.AsNoTracking().CountAsync()).ShouldBe(0);
        (await db.CatalogReleaseFiles.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RebuildPlatformAsync_ActivePendingSupersededClaims_ActiveWinsAndAllEvidenceSurvives()
    {
        using var db = CreateDb();
        SeedTitle(db, titleId: 10);
        var now = DateTimeOffset.UtcNow;
        var source = new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity
            {
                Id = 1,
                Kind = nameof(CatalogSourceKind.Dat),
                Status = nameof(CatalogSourceStatus.Active),
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            },
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };
        for (int id = 1; id <= 3; id++)
        {
            db.Files.Add(new FileEntityPersistence
            {
                Id = id,
                Sha256 = NewSha256((byte)(0x30 + id)),
                Size = 1,
                SizeOnDisk = 1,
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
            db.DatFiles.Add(new DatFileEntity
            {
                Id = id,
                Source = source,
                Lifecycle = id switch
                {
                    1 => nameof(DatFileLifecycle.Active),
                    2 => nameof(DatFileLifecycle.PendingActivation),
                    _ => nameof(DatFileLifecycle.Superseded)
                },
                SupersededAt = id == 3 ? now : null,
                Name = $"DAT {id}",
                Description = $"DAT {id}",
                Type = "NoIntro",
                PlatformId = PlatformId,
                OriginalFilename = $"{id}.dat",
                FileId = id,
                GameCount = 1,
                RomCount = 1,
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
        }

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1,
            CatalogSourceId = 1,
            EntryKey = "shared",
            Name = "Shared",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 1,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        var sha1 = NewSha1(0x7F);
        foreach ((int gameId, int datFileId, int romId) in new[] { (10, 1, 100), (20, 2, 200), (30, 3, 300) })
        {
            db.DatGames.Add(new DatGameEntity
            {
                Id = gameId,
                DatFileId = datFileId,
                SourceEntryId = 1,
                Name = "Shared",
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
            db.DatRoms.Add(new DatRomEntity
            {
                Id = romId,
                DatGameId = gameId,
                Name = "shared.rom",
                Size = 1024,
                Sha1 = sha1,
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
        }

        await db.SaveChangesAsync();

        (await CreateService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        (await db.CatalogReleaseSources.AsNoTracking().SingleAsync()).ProviderClaimKey.ShouldBe("10");
        var evidence = await db.CatalogReleaseFileSources.AsNoTracking()
            .OrderBy(row => row.ProviderClaimKey)
            .Select(row => row.ProviderClaimKey)
            .ToListAsync();
        evidence.ShouldBe(["10", "20", "30"]);
    }

    [Fact]
    public async Task RebuildPlatformAsync_SourceSnapshotRead_ParticipatesInRebuildTransaction()
    {
        using var db = CreateDb();
        EnsurePlatform(db);
        await db.SaveChangesAsync();
        var reader = new TransactionObservingSnapshotReader(db);
        var service = new CatalogProjectionService(
            db,
            reader,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            TimeProvider.System,
            Substitute.For<ILogger<CatalogProjectionService>>());

        bool rebuilt = await service.RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeTrue();
        reader.ObservedTransaction.ShouldBeTrue();
    }

    [Fact]
    public async Task RebuildPlatformAsync_RefreshesPayloadWhileDirtyBeforeMarkingClean()
    {
        using var db = CreateDb();
        EnsurePlatform(db);
        await db.SaveChangesAsync();
        var payload = new ObservingPayloadProjection(db);
        var service = new CatalogProjectionService(
            db,
            new StaticSnapshotReader(CatalogSourceSnapshot.Empty),
            payload,
            TimeProvider.System,
            Substitute.For<ILogger<CatalogProjectionService>>());

        (await service.RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        payload.ObservedState.ShouldBe(CatalogRebuildState.Dirty);
        (await db.Platforms.AsNoTracking().SingleAsync()).CatalogRebuildState
            .ShouldBe(CatalogRebuildState.Clean);
    }

    [Fact]
    public async Task RebuildPlatformAsync_PayloadRefreshFails_NeverMarksClean()
    {
        using var db = CreateDb();
        EnsurePlatform(db);
        await db.SaveChangesAsync();
        var payload = new ObservingPayloadProjection(db, throwOnRefresh: true);
        var service = new CatalogProjectionService(
            db,
            new StaticSnapshotReader(CatalogSourceSnapshot.Empty),
            payload,
            TimeProvider.System,
            Substitute.For<ILogger<CatalogProjectionService>>());

        (await service.RebuildPlatformAsync(PlatformId)).ShouldBeFalse();

        (await db.Platforms.AsNoTracking().SingleAsync()).CatalogRebuildState
            .ShouldBe(CatalogRebuildState.Failed);
    }

    [Fact]
    public async Task RebuildPlatformAsync_ReplacementFails_RollsBackPreviousProjectionGraph()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x78));
            await seedDb.SaveChangesAsync();
            (await CreateService(seedDb).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
            await seedDb.DatRoms
                .Where(rom => rom.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rom => rom.Sha1, NewSha1(0x79)));
        }

        int originalReleaseId;
        using (var beforeDb = CreateDb())
        {
            originalReleaseId = await beforeDb.CatalogReleases.Select(release => release.Id).SingleAsync();
        }

        using (var failingDb = CreateDb(new FailCatalogReleaseSaveInterceptor("replacement failed")))
        {
            (await CreateService(failingDb).RebuildPlatformAsync(PlatformId)).ShouldBeFalse();
        }

        using var assertDb = CreateDb();
        var release = await assertDb.CatalogReleases.AsNoTracking().SingleAsync();
        release.Id.ShouldBe(originalReleaseId);
        release.Fingerprint.ShouldBe($"sha1-v1:{NewSha1(0x78)}");
        (await assertDb.CatalogReleaseSources.AsNoTracking().SingleAsync()).ProviderClaimKey.ShouldBe("1");
        (await assertDb.CatalogReleaseFiles.AsNoTracking().SingleAsync()).Sha1.ShouldBe(NewSha1(0x78));
        (await assertDb.CatalogReleaseFileSources.AsNoTracking().SingleAsync()).ProviderRequirementKey.ShouldBe("1");
        (await assertDb.Platforms.AsNoTracking().SingleAsync()).CatalogRebuildState
            .ShouldBe(CatalogRebuildState.Failed);
    }

    [Fact]
    public async Task RebuildPlatformAsync_CanceledAfterFirstDestructiveStatement_RollsBackProjectionGraph()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x7A));
            await seedDb.SaveChangesAsync();
            (await CreateService(seedDb).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
            await seedDb.DatRoms
                .Where(rom => rom.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rom => rom.Sha1, NewSha1(0x7B)));
        }

        int originalReleaseId;
        using (var beforeDb = CreateDb())
        {
            originalReleaseId = await beforeDb.CatalogReleases.Select(release => release.Id).SingleAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterCatalogFileDeleteInterceptor(cancellation);
        using (var cancelingDb = CreateDb(interceptor))
        {
            await Should.ThrowAsync<OperationCanceledException>(
                CreateService(cancelingDb).RebuildPlatformAsync(PlatformId, cancellation.Token));
        }

        cancellation.IsCancellationRequested.ShouldBeTrue();
        using var assertDb = CreateDb();
        var release = await assertDb.CatalogReleases.AsNoTracking().SingleAsync();
        release.Id.ShouldBe(originalReleaseId);
        release.Fingerprint.ShouldBe($"sha1-v1:{NewSha1(0x7A)}");
        (await assertDb.CatalogReleaseSources.AsNoTracking().SingleAsync()).ProviderClaimKey.ShouldBe("1");
        (await assertDb.CatalogReleaseFiles.AsNoTracking().SingleAsync()).Sha1.ShouldBe(NewSha1(0x7A));
        (await assertDb.CatalogReleaseFileSources.AsNoTracking().SingleAsync()).ProviderRequirementKey.ShouldBe("1");
        (await assertDb.Platforms.AsNoTracking().SingleAsync()).CatalogRebuildState
            .ShouldBe(CatalogRebuildState.Dirty);
    }

    [Fact]
    public async Task RebuildPlatformAsync_RebuildFails_PersistsErrorAndFailureTimestamp()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
            await seedDb.SaveChangesAsync();
        }

        var failedAt = new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(failedAt);
        var interceptor = new FailCatalogReleaseSaveInterceptor("catalog rebuild exploded");
        using var db = CreateDb(interceptor);

        bool rebuilt = await CreateService(db, timeProvider).RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeFalse();
        using var assertDb = CreateDb();
        var platform = await assertDb.Platforms.AsNoTracking().SingleAsync(p => p.Id == PlatformId);
        platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Failed);
        platform.CatalogRebuildError.ShouldBe("catalog rebuild exploded");
        platform.CatalogRebuildFailedAtUtc.ShouldBe(failedAt);
    }

    [Fact]
    public async Task RebuildPlatformAsync_SuccessAfterFailure_ClearsErrorAndFailureTimestamp()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
            await seedDb.SaveChangesAsync();
        }

        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero));
        var interceptor = new FailCatalogReleaseSaveInterceptor("transient failure");
        using var db = CreateDb(interceptor);
        (await CreateService(db, timeProvider).RebuildPlatformAsync(PlatformId)).ShouldBeFalse();

        interceptor.Enabled = false;
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var rebuiltAt = timeProvider.GetUtcNow();

        (await CreateService(db, timeProvider).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        using var assertDb = CreateDb();
        var platform = await assertDb.Platforms.AsNoTracking().SingleAsync(p => p.Id == PlatformId);
        platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        platform.CatalogRebuildError.ShouldBeNull();
        platform.CatalogRebuildFailedAtUtc.ShouldBeNull();
        platform.CatalogRebuiltAt.ShouldBe(rebuiltAt);
    }

    [Fact]
    public async Task RebuildPlatformAsync_LongErrorMessage_TruncatesToBound()
    {
        using (var seedDb = CreateDb())
        {
            SeedTitle(seedDb, titleId: 10);
            SeedMappedGame(seedDb, datFileId: 1, gameId: 1, romId: 1, titleId: 10, NewSha1(0x11));
            await seedDb.SaveChangesAsync();
        }

        string longMessage = new('x', 2500);
        var interceptor = new FailCatalogReleaseSaveInterceptor(longMessage);
        using var db = CreateDb(interceptor);

        bool rebuilt = await CreateService(db, new ManualTimeProvider(DateTimeOffset.UtcNow))
            .RebuildPlatformAsync(PlatformId);

        rebuilt.ShouldBeFalse();
        using var assertDb = CreateDb();
        var platform = await assertDb.Platforms.AsNoTracking().SingleAsync(p => p.Id == PlatformId);
        platform.CatalogRebuildError.ShouldNotBeNull();
        platform.CatalogRebuildError.Length.ShouldBe(2000);
        platform.CatalogRebuildError.ShouldBe(longMessage[..2000]);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private RomdDbContext CreateDb(IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options);

    private static CatalogProjectionService CreateService(RomdDbContext db) =>
        CatalogProjectionTestFactory.Create(db);

    private static CatalogProjectionService CreateService(RomdDbContext db, TimeProvider timeProvider) =>
        CatalogProjectionTestFactory.Create(db, timeProvider);

    private sealed class FailCatalogReleaseSaveInterceptor(string message) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<CatalogReleaseEntity>()
                .Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException(message);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TransactionObservingSnapshotReader(RomdDbContext context) : ICatalogSourceSnapshotReader
    {
        public bool ObservedTransaction { get; private set; }

        public Task<CatalogSourceSnapshot> ReadPlatformAsync(
            int platformId,
            CancellationToken cancellationToken = default)
        {
            ObservedTransaction = context.Database.CurrentTransaction is not null;
            return Task.FromResult(CatalogSourceSnapshot.Empty);
        }
    }

    private sealed class CancelAfterCatalogFileDeleteInterceptor(
        CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        private int _hasCanceled;

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM romd.\"CatalogReleaseFiles\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _hasCanceled, 1) == 0)
            {
                await cancellation.CancelAsync();
            }

            return result;
        }
    }

    private static void SeedTitle(RomdDbContext db, int titleId, bool isTracked = false)
    {
        EnsurePlatform(db);
        db.Titles.Add(new TitleEntity
        {
            Id = titleId,
            PlatformId = PlatformId,
            Name = $"Title {titleId}",
            NormalizedName = $"title {titleId}",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemActor.UserId
        });
        if (isTracked)
        {
            db.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = titleId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static void SeedMappedGame(
        RomdDbContext db,
        int datFileId,
        int gameId,
        int romId,
        int titleId,
        Sha1? sha1,
        string romName = "game.sfc",
        string? gameName = null,
        CatalogSourceStatus sourceStatus = CatalogSourceStatus.Active,
        bool isBios = false)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = SystemActor.UserId;

        EnsurePlatform(db);

        if (!db.Files.Local.Any(f => f.Id == datFileId) && !db.Files.Any(f => f.Id == datFileId))
        {
            db.Files.Add(new FileEntityPersistence
            {
                Id = datFileId,
                Sha256 = NewSha256((byte)datFileId),
                Size = 1,
                SizeOnDisk = 1,
                IsCompressed = false,
                CreatedAt = now,
                CreatedByUserId = userId
            });
        }

        if (!db.DatFiles.Local.Any(f => f.Id == datFileId) && !db.DatFiles.Any(f => f.Id == datFileId))
        {
            db.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity
                    {
                        Id = datFileId,
                        Kind = "Dat",
                        Status = sourceStatus.ToString()
                    }
                },
                Id = datFileId,
                Name = $"DAT {datFileId}",
                Description = $"DAT {datFileId}",
                Type = "NoIntro",
                PlatformId = PlatformId,
                OriginalFilename = $"dat-{datFileId}.dat",
                FileId = datFileId,
                GameCount = 1,
                RomCount = 1,
                DiskCount = 0,
                CreatedAt = now,
                CreatedByUserId = userId
            });
        }

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = datFileId,
            EntryKey = gameName ?? $"Game {gameId}",
            Name = gameName ?? $"Game {gameId}",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = datFileId,
            SourceEntryId = gameId,
            Name = gameName ?? $"Game {gameId}",
            IsBios = isBios,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = romId,
            DatGameId = gameId,
            Name = romName,
            Size = 1024,
            Sha1 = sha1,
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

    private static void SeedMappedDiskGame(
        RomdDbContext db,
        int datFileId,
        int gameId,
        int diskId,
        int titleId,
        Sha1 sha1)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = SystemActor.UserId;

        EnsurePlatform(db);

        if (!db.Files.Local.Any(f => f.Id == datFileId) && !db.Files.Any(f => f.Id == datFileId))
        {
            db.Files.Add(new FileEntityPersistence
            {
                Id = datFileId,
                Sha256 = NewSha256((byte)datFileId),
                Size = 1,
                SizeOnDisk = 1,
                IsCompressed = false,
                CreatedAt = now,
                CreatedByUserId = userId
            });
        }

        if (!db.DatFiles.Local.Any(f => f.Id == datFileId) && !db.DatFiles.Any(f => f.Id == datFileId))
        {
            db.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Id = datFileId, Kind = "Dat", Status = "Active" }
                },
                Id = datFileId,
                Name = $"DAT {datFileId}",
                Description = $"DAT {datFileId}",
                Type = "Redump",
                PlatformId = PlatformId,
                OriginalFilename = $"dat-{datFileId}.dat",
                FileId = datFileId,
                GameCount = 1,
                RomCount = 0,
                DiskCount = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            });
        }

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = datFileId,
            EntryKey = $"Disk Game {gameId}",
            Name = $"Disk Game {gameId}",
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = datFileId,
            SourceEntryId = gameId,
            Name = $"Disk Game {gameId}",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatDisks.Add(new DatDiskEntity
        {
            Id = diskId,
            DatGameId = gameId,
            Name = $"disk-{gameId}",
            Sha1 = sha1,
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

    private static void EnsurePlatform(RomdDbContext db)
    {
        if (db.Platforms.Local.Any(p => p.Id == PlatformId) || db.Platforms.Any(p => p.Id == PlatformId))
        {
            return;
        }

        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemActor.UserId
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

    private sealed class ObservingPayloadProjection(
        RomdDbContext context,
        bool throwOnRefresh = false) : ITitlePayloadAvailabilityProjection
    {
        public CatalogRebuildState? ObservedState { get; private set; }

        public Task RefreshPayloadAssertionsAsync(
            IReadOnlyCollection<int> titleIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RefreshSourceEntryPayloadAssertionsAsync(
            IReadOnlyCollection<int> sourceEntryIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RefreshCatalogSourcePayloadAssertionsAsync(
            int catalogSourceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RefreshPlatformPayloadAssertionsAsync(
            int platformId,
            CancellationToken cancellationToken = default) =>
            ObserveAsync(platformId, cancellationToken);

        public Task RollupTitlesAsync(
            IReadOnlyCollection<int> titleIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RollupTitleRangeAsync(
            int afterTitleId,
            int throughTitleId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task RollupPlatformAsync(
            int platformId,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(platformId, cancellationToken);
        }

        private async Task ObserveAsync(int platformId, CancellationToken cancellationToken)
        {
            ObservedState = await context.Platforms
                .Where(platform => platform.Id == platformId)
                .Select(platform => platform.CatalogRebuildState)
                .SingleAsync(cancellationToken);
            if (throwOnRefresh)
            {
                throw new InvalidOperationException("payload refresh failed");
            }
        }

        public Task RollupCatalogSourceAsync(
            int catalogSourceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PayloadAvailabilityAuditResult> AuditBatchAsync(
            int? afterTitleId,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StaticSnapshotReader(CatalogSourceSnapshot snapshot) : ICatalogSourceSnapshotReader
    {
        public Task<CatalogSourceSnapshot> ReadPlatformAsync(
            int platformId,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
