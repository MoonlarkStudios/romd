using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>
///     Convergence proofs for the recurring DAT replacement sweep: stale replace jobs fail
///     terminally with attempt errors preserved, abandoned pending versions age out to the
///     design's repairable state (old stays Active, new discarded), the retention backstop
///     re-runs idempotently, the orphaned-title backstop applies the retain-or-delete
///     policy behind a creation-age grace window, and one failing duty never blocks the
///     others.
/// </summary>
public sealed class DatReplacementConvergenceSweepJobTests : IDisposable
{
    private const string TimeoutMessage = "Job timed out (no progress updates received)";

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public DatReplacementConvergenceSweepJobTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ExecuteAsync_StaleStartedReplaceJob_FailsTerminallyPreservingAttemptErrors()
    {
        // Whole-second timestamps survive the SQLite text roundtrip exactly.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var staleId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var terminalCompletedAt = now - TimeSpan.FromHours(2);
        await using (var db = CreateDb())
        {
            SeedReplaceJob(db, staleId, ReplaceDatPhase.Replacing, startedAt: now - TimeSpan.FromHours(1),
                updatedAt: now - TimeSpan.FromMinutes(31),
                errorsJson: """[{"Item":"attempt","Message":"activation failed","OccurredAt":"2026-08-19T00:00:00+00:00"}]""");
            SeedReplaceJob(db, recentId, ReplaceDatPhase.Replacing, startedAt: now - TimeSpan.FromHours(1),
                updatedAt: now - TimeSpan.FromMinutes(1));
            SeedReplaceJob(db, terminalId, ReplaceDatPhase.Cancelled, startedAt: now - TimeSpan.FromHours(3),
                updatedAt: now - TimeSpan.FromHours(2), completedAt: terminalCompletedAt);
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        var failed = await LoadReplaceJobAsync(readDb, staleId);
        failed.PhaseEnum.ShouldBe(ReplaceDatPhase.Failed);
        failed.CompletedAt.ShouldNotBeNull();
        failed.Errors.ShouldContain(error => error.Item == "attempt" && error.Message == "activation failed");
        failed.Errors.ShouldContain(error => error.Item == "job" && error.Message == TimeoutMessage);

        var recent = await LoadReplaceJobAsync(readDb, recentId);
        recent.PhaseEnum.ShouldBe(ReplaceDatPhase.Replacing);
        recent.HasErrors.ShouldBeFalse();

        var terminal = await LoadReplaceJobAsync(readDb, terminalId);
        terminal.PhaseEnum.ShouldBe(ReplaceDatPhase.Cancelled);
        terminal.CompletedAt.ShouldBe(terminalCompletedAt);
        terminal.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_AbandonedAgedPendingVersion_ConvergesToOldActiveAndReleasesFileForOrphanCleanup()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            await SeedSourceWithActiveAndPendingAsync(db, pendingImportedAt: now - TimeSpan.FromHours(25));
            // A retry-exhausted job already failed by duty 1 does not protect the version.
            SeedReplaceJob(db, Guid.NewGuid(), ReplaceDatPhase.Failed, startedAt: now - TimeSpan.FromDays(2),
                updatedAt: now - TimeSpan.FromDays(2), completedAt: now - TimeSpan.FromDays(2), newDatId: 8);
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        // Converged state: old version still Active with its graph, pending row and graph
        // gone, source anchor intact.
        (await readDb.DatFiles.AsNoTracking().Select(d => d.Id).ToListAsync()).ShouldBe([7]);
        (await readDb.DatFiles.AsNoTracking().SingleAsync(d => d.Id == 7))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
        (await readDb.DatGames.AsNoTracking().AnyAsync(g => g.DatFileId == 7)).ShouldBeTrue();
        (await readDb.DatGames.AsNoTracking().AnyAsync(g => g.DatFileId == 8)).ShouldBeFalse();
        (await readDb.DatRoms.AsNoTracking().AnyAsync(r => r.DatGameId == 80)).ShouldBeFalse();
        (await readDb.DatSources.AsNoTracking().CountAsync()).ShouldBe(1);

        // The sweep never deletes blobs: the file row survives, but dropping the DatFile
        // reference makes it eligible for the existing daily orphan cleanup.
        (await readDb.Files.AsNoTracking().AnyAsync(f => f.Id == 51)).ShouldBeTrue();
        var fileRepository = new FileRepository(readDb);
        var reclaimable = await fileRepository.GetUnreferencedFileIdsOlderThanAsync(now, CancellationToken.None);
        reclaimable.ShouldContain(51);
        reclaimable.ShouldNotContain(17);
    }

    [Fact]
    public async Task ExecuteAsync_AbandonedPendingClaims_DeletesAndDirtiesCatalogAndLibrariesAtomically()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            await SeedSourceWithActiveAndPendingAsync(db, pendingImportedAt: now - TimeSpan.FromHours(25));
            await db.DatFiles.ExecuteUpdateAsync(setters => setters.SetProperty(dat => dat.PlatformId, 3));
            db.Libraries.Add(Library(id: 1));
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories(new ManualTimeProvider(now)).ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.AnyAsync(dat => dat.Id == 8)).ShouldBeFalse();
        (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await readDb.Libraries.SingleAsync(library => library.Id == 1))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AbandonedPendingDirtyingFails_RollsBackDeletionAndCatalogState()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            await SeedSourceWithActiveAndPendingAsync(db, pendingImportedAt: now - TimeSpan.FromHours(25));
            await db.DatFiles.ExecuteUpdateAsync(setters => setters.SetProperty(dat => dat.PlatformId, 3));
            db.Libraries.Add(Library(id: 1));
            await db.SaveChangesAsync();
        }

        var failingMaterialization = Substitute.For<ILibraryMaterializationService>();
        failingMaterialization
            .FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("flag failed"));
        await NewSweepWithRealRepositories(
                new ManualTimeProvider(now),
                services => services.AddSingleton(failingMaterialization))
            .ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.AnyAsync(dat => dat.Id == 8)).ShouldBeTrue();
        (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await readDb.Libraries.SingleAsync(library => library.Id == 1))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PendingVersionReferencedByLiveJob_IsNotDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            await SeedSourceWithActiveAndPendingAsync(db, pendingImportedAt: now - TimeSpan.FromHours(25));
            // A live job with recent checkpoint progress still owns the pending version.
            SeedReplaceJob(db, Guid.NewGuid(), ReplaceDatPhase.Replacing, startedAt: now - TimeSpan.FromHours(25),
                updatedAt: now, newDatId: 8);
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.AsNoTracking().SingleAsync(d => d.Id == 8))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await readDb.DatGames.AsNoTracking().AnyAsync(g => g.DatFileId == 8)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PendingVersionYoungerThanThreshold_IsNotDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            await SeedSourceWithActiveAndPendingAsync(db, pendingImportedAt: now - TimeSpan.FromHours(1));
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.AsNoTracking().SingleAsync(d => d.Id == 8))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await readDb.DatGames.AsNoTracking().AnyAsync(g => g.DatFileId == 8)).ShouldBeTrue();
    }

    [Fact]
    public async Task PendingDiscovery_LiveJobAndNoClaims_StillIncludesRoutedPlatformSuperset()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDb();
        SeedPlatform(db, platformId: 3);
        SeedFile(db, fileId: 17, createdAt: now - TimeSpan.FromDays(2));
        var source = new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
            CreatedAt = now - TimeSpan.FromDays(2)
        };
        SeedVersion(
            db,
            datId: 8,
            fileId: 17,
            source,
            DatFileLifecycle.PendingActivation,
            importedAt: now - TimeSpan.FromHours(25),
            platformId: 3);
        SeedReplaceJob(
            db,
            Guid.NewGuid(),
            ReplaceDatPhase.Replacing,
            startedAt: now - TimeSpan.FromHours(25),
            updatedAt: now,
            newDatId: 8);
        await db.SaveChangesAsync();

        var platforms = await new DatRepository(db, TimeProvider.System)
            .GetRoutedPlatformIdsWithAgedPendingVersionsAsync(now - TimeSpan.FromHours(24));

        platforms.ShouldBe([3]);
        (await db.DatGames.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RetentionDiscovery_AllRoutedLifecycles_FormsSafeSourceSuperset()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDb();
        SeedPlatform(db, platformId: 3);
        SeedPlatform(db, platformId: 4);
        var source = new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
            CreatedAt = now
        };
        for (int id = 1; id <= 3; id++)
        {
            SeedFile(db, fileId: id, createdAt: now);
        }

        SeedVersion(db, 1, 1, source, DatFileLifecycle.Active, now, platformId: 3);
        SeedVersion(db, 2, 2, source, DatFileLifecycle.PendingActivation, now, platformId: 4);
        SeedVersion(db, 3, 3, source, DatFileLifecycle.Superseded, now, now, platformId: 3);
        await db.SaveChangesAsync();

        var platforms = await new DatRepository(db, TimeProvider.System).GetRoutedPlatformIdsBySourceIdAsync(1);

        platforms.ShouldBe([3, 4], ignoreOrder: true);
    }

    [Fact]
    public async Task ExecuteAsync_OverRetentionSupersededSet_ConvergesToNewestOnlyAndReRunIsNoOp()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            db.Libraries.Add(Library(id: 1));
            SeedFile(db, fileId: 17, createdAt: now - TimeSpan.FromDays(3));
            var source = new DatSourceEntity
            {
                Id = 1,
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
                CreatedAt = now - TimeSpan.FromDays(3)
            };
            SeedVersion(db, datId: 7, fileId: 17, source, DatFileLifecycle.Active, importedAt: now);
            for (int i = 1; i <= 3; i++)
            {
                SeedFile(db, fileId: 17 + i, createdAt: now - TimeSpan.FromDays(3));
                SeedVersion(db, datId: 7 + i, fileId: 17 + i, source, DatFileLifecycle.Superseded,
                    importedAt: now - TimeSpan.FromDays(3), supersededAt: now - TimeSpan.FromHours(4 - i),
                    platformId: 3);
            }

            await db.SaveChangesAsync();
        }

        var sweep = NewSweepWithRealRepositories();
        await sweep.ExecuteAsync(CancellationToken.None);

        await using (var readDb = CreateDb())
        {
            var superseded = await readDb.DatFiles.AsNoTracking()
                .Where(d => d.Lifecycle == nameof(DatFileLifecycle.Superseded))
                .Select(d => d.Id)
                .ToListAsync();
            superseded.ShouldBe([10]); // Newest superseded (latest SupersededAt) retained.
            (await readDb.DatFiles.AsNoTracking().SingleAsync(d => d.Id == 7))
                .Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
            // Even claimless versions use the source-wide safe superset: conservative Dirty
            // is intentional because ReadCommitted can change claim/winner state at the seam.
            (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
                .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
            (await readDb.Libraries.SingleAsync(library => library.Id == 1))
                .NeedsMaterialization.ShouldBeTrue();
        }

        // Re-run is a no-op.
        await sweep.ExecuteAsync(CancellationToken.None);

        await using (var rerunDb = CreateDb())
        {
            (await rerunDb.DatFiles.AsNoTracking().Select(d => d.Id).OrderBy(id => id).ToListAsync())
                .ShouldBe([7, 10]);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OverRetentionVersionWithClaims_DirtiesCatalogAndLibraries()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            db.Libraries.Add(Library(id: 1));
            var source = new DatSourceEntity
            {
                Id = 1,
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
                CreatedAt = now - TimeSpan.FromDays(3)
            };
            for (int id = 7; id <= 10; id++)
            {
                SeedFile(db, fileId: id + 10, createdAt: now - TimeSpan.FromDays(3));
                SeedVersion(
                    db,
                    datId: id,
                    fileId: id + 10,
                    source,
                    id == 7 ? DatFileLifecycle.Active : DatFileLifecycle.Superseded,
                    importedAt: now - TimeSpan.FromDays(3),
                    supersededAt: id == 7 ? null : now - TimeSpan.FromHours(11 - id),
                    platformId: 3);
            }

            SeedGameWithRom(db, gameId: 80, datFileId: 8, firstHashByte: 8);
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.AnyAsync(dat => dat.Id == 8)).ShouldBeFalse();
        (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await readDb.Libraries.SingleAsync(library => library.Id == 1))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PreflightFalseThenNewSupersededAppears_SweepRepairsRace()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            db.Libraries.Add(Library(id: 1));
            var source = new DatSourceEntity
            {
                Id = 1,
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
                CreatedAt = now
            };
            SeedFile(db, fileId: 17, createdAt: now);
            SeedFile(db, fileId: 18, createdAt: now);
            SeedVersion(db, 7, 17, source, DatFileLifecycle.Active, now, platformId: 3);
            SeedVersion(db, 8, 18, source, DatFileLifecycle.Superseded, now,
                supersededAt: now - TimeSpan.FromMinutes(2), platformId: 3);
            await db.SaveChangesAsync();

            // This is the coordinator's no-op preflight observation.
            (await new DatRepository(db, TimeProvider.System)
                    .HasSupersededVersionsBeyondRetentionAsync(1))
                .ShouldBeFalse();
        }

        // A concurrent replacement crosses the limit after the false observation. Immediate
        // cleanup may return zero, but the recurring source scan remains the durable backstop.
        await using (var db = CreateDb())
        {
            SeedFile(db, fileId: 19, createdAt: now);
            db.DatFiles.Add(new DatFileEntity
            {
                Id = 9,
                DatSourceId = 1,
                Name = "DAT 9",
                Description = "DAT 9",
                Type = nameof(DatType.NoIntro),
                OriginalFilename = "dat-9.dat",
                FileId = 19,
                PlatformId = 3,
                Lifecycle = nameof(DatFileLifecycle.Superseded),
                SupersededAt = now - TimeSpan.FromMinutes(1),
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories().ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.OrderBy(file => file.Id).Select(file => file.Id).ToListAsync())
            .ShouldBe([7, 9]);
        (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await readDb.Libraries.SingleAsync(library => library.Id == 1))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_RetentionInvalidationFails_RollsBackVersionDeletion()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            db.Libraries.Add(Library(id: 1));
            var source = new DatSourceEntity
            {
                Id = 1,
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
                CreatedAt = now
            };
            for (int id = 7; id <= 10; id++)
            {
                SeedFile(db, fileId: id + 10, createdAt: now);
                SeedVersion(
                    db,
                    datId: id,
                    fileId: id + 10,
                    source,
                    id == 7 ? DatFileLifecycle.Active : DatFileLifecycle.Superseded,
                    importedAt: now,
                    supersededAt: id == 7 ? null : now - TimeSpan.FromHours(11 - id),
                    platformId: 3);
            }

            await db.SaveChangesAsync();
        }

        var failingMaterialization = Substitute.For<ILibraryMaterializationService>();
        failingMaterialization
            .FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("flag failed"));
        await NewSweepWithRealRepositories(
                configure: services => services.AddSingleton(failingMaterialization))
            .ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.DatFiles.OrderBy(dat => dat.Id).Select(dat => dat.Id).ToListAsync())
            .ShouldBe([7, 8, 9, 10]);
        (await readDb.Platforms.SingleAsync(platform => platform.Id == 3))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await readDb.Libraries.SingleAsync(library => library.Id == 1))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_OrphanedTitlesPastGrace_DeletesUntrackedAndRetainsTrackedAsUserOnly()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            // Both titles lost their last source link outside a policy-applying command
            // and are well past the one-hour derivation grace window.
            SeedTitle(db, titleId: 301, platformId: 3, createdAt: now - TimeSpan.FromHours(2));
            SeedTitle(db, titleId: 302, platformId: 3, createdAt: now - TimeSpan.FromHours(2));
            db.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = 302,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories(new ManualTimeProvider(now)).ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.Titles.AsNoTracking().AnyAsync(t => t.Id == 301)).ShouldBeFalse();
        var retained = await readDb.Titles.AsNoTracking().SingleAsync(t => t.Id == 302);
        retained.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await readDb.TrackedTitles.AsNoTracking().AnyAsync(t => t.TitleId == 302)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_OrphanBackstopScope_SparesGraceWindowUserOnlyAndLinkedTitles()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using (var db = CreateDb())
        {
            SeedPlatform(db, platformId: 3);
            // Younger than the one-hour grace: possibly mid-derivation, never touched.
            SeedTitle(db, titleId: 303, platformId: 3, createdAt: now - TimeSpan.FromMinutes(30));
            // Already retained as UserOnly with no user state left: if the backstop
            // reprocessed it, DeleteOrRetain would hard-delete it — it must be skipped.
            SeedTitle(db, titleId: 304, platformId: 3, createdAt: now - TimeSpan.FromHours(2),
                catalogState: TitleCatalogState.UserOnly);
            // Old but still linked: not an orphan.
            SeedTitle(db, titleId: 305, platformId: 3, createdAt: now - TimeSpan.FromHours(2));
            db.CatalogSources.Add(new CatalogSourceEntity { Id = 900, Kind = "Dat", Status = "Active" });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = 900,
                CatalogSourceId = 900,
                EntryKey = "Linked Game",
                Name = "Linked Game",
                PlatformId = 3,
                CreatedAt = now - TimeSpan.FromHours(2)
            });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 900, TitleId = 305 });
            await db.SaveChangesAsync();
        }

        await NewSweepWithRealRepositories(new ManualTimeProvider(now)).ExecuteAsync(CancellationToken.None);

        await using var readDb = CreateDb();
        (await readDb.Titles.AsNoTracking().SingleAsync(t => t.Id == 303))
            .CatalogState.ShouldBe(TitleCatalogState.Active);
        (await readDb.Titles.AsNoTracking().SingleAsync(t => t.Id == 304))
            .CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await readDb.Titles.AsNoTracking().SingleAsync(t => t.Id == 305))
            .CatalogState.ShouldBe(TitleCatalogState.Active);
        (await readDb.TitleSourceLinks.AsNoTracking().AnyAsync(l => l.TitleId == 305)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_FirstDutyFails_SecondAndThirdDutiesStillRun()
    {
        var replaceJobRepository = Substitute.For<IReplaceDatJobRepository>();
        replaceJobRepository
            .GetStaleStartedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("duty one unavailable"));
        var datRepository = Substitute.For<IDatRepository>();
        datRepository.GetSourceIdsExceedingSupersededRetentionAsync(Arg.Any<CancellationToken>())
            .Returns([5]);
        var sweep = NewSweep(replaceJobRepository, datRepository, TimeProvider.System);

        await Should.NotThrowAsync(() => sweep.ExecuteAsync(CancellationToken.None));

        await datRepository.Received(1)
            .DeleteAbandonedPendingVersionsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await datRepository.Received(1)
            .DeleteSupersededVersionsBeyondMostRecentAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_JobBecomesTerminalBetweenPendingDiscoveryAndDelete_DirtiesDiscoveredPlatform()
    {
        var replaceJobs = Substitute.For<IReplaceDatJobRepository>();
        replaceJobs.GetStaleStartedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([]);
        var datRepository = Substitute.For<IDatRepository>();
        datRepository.GetRoutedPlatformIdsWithAgedPendingVersionsAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([3]);
        bool jobBecameTerminal = false;
        datRepository.DeleteAbandonedPendingVersionsAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                jobBecameTerminal = true;
                return 1;
            });
        datRepository.GetSourceIdsExceedingSupersededRetentionAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        var projection = Substitute.For<ICatalogProjectionService>();
        var materialization = Substitute.For<ILibraryMaterializationService>();
        var sweep = NewSweep(
            replaceJobs,
            datRepository,
            TimeProvider.System,
            projection,
            materialization);

        await sweep.ExecuteAsync(CancellationToken.None);

        jobBecameTerminal.ShouldBeTrue();
        await projection.Received(1).RefreshPlatformPayloadAsync(3, Arg.Any<CancellationToken>());
        await projection.Received(1).MarkPlatformDirtyAsync(3, Arg.Any<CancellationToken>());
        await materialization.Received(1).FlagAffectedLibrariesAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDuringDuty_PropagatesWithoutRunningLaterDuties()
    {
        using var cancellation = new CancellationTokenSource();
        var replaceJobRepository = Substitute.For<IReplaceDatJobRepository>();
        replaceJobRepository
            .GetStaleStartedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReplaceDatJob>>(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var datRepository = Substitute.For<IDatRepository>();
        var sweep = NewSweep(replaceJobRepository, datRepository, TimeProvider.System);

        // Shutdown cancellation is not a duty failure: it propagates instead of being
        // swallowed by the per-duty isolation, and later duties do not run.
        await Should.ThrowAsync<OperationCanceledException>(() => sweep.ExecuteAsync(cancellation.Token));

        await datRepository.DidNotReceiveWithAnyArgs()
            .DeleteAbandonedPendingVersionsAsync(default, default);
        await datRepository.DidNotReceiveWithAnyArgs()
            .DeleteSupersededVersionsBeyondMostRecentAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_RunsDutiesInOrderWithThirtyMinuteStaleAndTwentyFourHourAgeThresholds()
    {
        var fixedNow = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var replaceJobRepository = Substitute.For<IReplaceDatJobRepository>();
        replaceJobRepository.GetStaleStartedAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var datRepository = Substitute.For<IDatRepository>();
        datRepository.GetSourceIdsExceedingSupersededRetentionAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        var sweep = NewSweep(replaceJobRepository, datRepository, new ManualTimeProvider(fixedNow));

        await sweep.ExecuteAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            replaceJobRepository.GetStaleStartedAsync(TimeSpan.FromMinutes(30), Arg.Any<CancellationToken>());
            datRepository.DeleteAbandonedPendingVersionsAsync(
                fixedNow - TimeSpan.FromHours(24), Arg.Any<CancellationToken>());
            datRepository.GetSourceIdsExceedingSupersededRetentionAsync(Arg.Any<CancellationToken>());
        });
    }

    private DatReplacementConvergenceSweepJob NewSweepWithRealRepositories(
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configure = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDb());
        services.AddSingleton(time);
        services.AddScoped<IReplaceDatJobRepository, ReplaceDatJobRepository>();
        services.AddScoped<IDatRepository, DatRepository>();
        services.AddScoped<ITitleRepository, TitleRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<ISourceLifecycle, SourceLifecycleStore>();
        services.AddScoped<ICatalogProjectionService, SweepCatalogProjectionService>();
        services.AddScoped<ILibraryMaterializationService, SweepLibraryMaterializationService>();
        services.AddScoped<IDatVersionRetentionCoordinator, DatVersionRetentionCoordinator>();
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();

        return new DatReplacementConvergenceSweepJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            NullLogger<DatReplacementConvergenceSweepJob>.Instance);
    }

    private static DatReplacementConvergenceSweepJob NewSweep(
        IReplaceDatJobRepository replaceJobRepository,
        IDatRepository datRepository,
        TimeProvider timeProvider,
        ICatalogProjectionService? projection = null,
        ILibraryMaterializationService? materialization = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(replaceJobRepository);
        services.AddSingleton(datRepository);
        var transaction = Substitute.For<ITransaction>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        services.AddSingleton(unitOfWork);
        services.AddSingleton(projection ?? Substitute.For<ICatalogProjectionService>());
        services.AddSingleton(materialization ?? Substitute.For<ILibraryMaterializationService>());
        services.AddSingleton<IDatVersionRetentionCoordinator>(new DelegatingRetentionCoordinator(datRepository));
        var provider = services.BuildServiceProvider();

        return new DatReplacementConvergenceSweepJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<DatReplacementConvergenceSweepJob>.Instance);
    }

    private sealed class SweepCatalogProjectionService(RomdDbContext context) : ICatalogProjectionService
    {
        public Task<bool> RebuildPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task MarkPlatformDirtyAsync(
            int platformId,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<int>? affectedTitleIds = null) =>
            await context.Platforms
                .Where(platform => platform.Id == platformId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        platform => platform.CatalogRebuildState,
                        CatalogRebuildState.Dirty),
                    cancellationToken);

        public Task MarkCatalogSourceDirtyAsync(
            int platformId,
            int catalogSourceId,
            CancellationToken cancellationToken = default) =>
            MarkPlatformDirtyAsync(platformId, cancellationToken);

        public Task RefreshCatalogSourcePayloadAsync(
            int catalogSourceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshPlatformPayloadAsync(
            int platformId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<int>> GetPlatformIdsNeedingRebuildAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SweepLibraryMaterializationService(RomdDbContext context) : ILibraryMaterializationService
    {
        public Task<MaterializationResult> MaterializeAsync(
            int libraryId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async Task FlagAffectedLibrariesAsync(int? platformId, CancellationToken ct = default) =>
            await context.Libraries.ExecuteUpdateAsync(
                setters => setters.SetProperty(library => library.NeedsMaterialization, true),
                ct);
    }

    private sealed class DelegatingRetentionCoordinator(IDatRepository repository) : IDatVersionRetentionCoordinator
    {
        public Task<int> EnforceAsync(int datSourceId, CancellationToken cancellationToken = default) =>
            repository.DeleteSupersededVersionsBeyondMostRecentAsync(datSourceId, cancellationToken);
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static async Task<ReplaceDatJob> LoadReplaceJobAsync(RomdDbContext db, Guid id) =>
        (await db.Jobs.AsNoTracking().OfType<ReplaceDatJobEntity>().SingleAsync(j => j.Id == id)).ToDomain();

    private static void SeedReplaceJob(
        RomdDbContext db,
        Guid id,
        ReplaceDatPhase phase,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt = null,
        int? newDatId = null,
        string errorsJson = "[]")
    {
        db.Set<ReplaceDatJobEntity>().Add(new ReplaceDatJobEntity
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ExistingDatId = 7,
            NewDatId = newDatId,
            SourceFilename = "replacement.dat",
            Phase = phase.ToString(),
            ErrorsJson = errorsJson,
            CreatedAt = startedAt,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            UpdatedAt = updatedAt,
            JobType = "replace_dat"
        });
    }

    /// <summary>
    ///     Seeds one source with an Active version (DAT 7, file 17, game 70) and a
    ///     PendingActivation version (DAT 8, file 51, game 80) imported at the given time.
    /// </summary>
    private static async Task SeedSourceWithActiveAndPendingAsync(
        RomdDbContext db,
        DateTimeOffset pendingImportedAt)
    {
        var now = DateTimeOffset.UtcNow;
        SeedFile(db, fileId: 17, createdAt: now - TimeSpan.FromDays(3));
        SeedFile(db, fileId: 51, createdAt: pendingImportedAt);
        var source = new DatSourceEntity
        {
            Id = 1,
            CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" },
            CreatedAt = now - TimeSpan.FromDays(3)
        };
        SeedVersion(db, datId: 7, fileId: 17, source, DatFileLifecycle.Active, importedAt: now - TimeSpan.FromDays(3));
        SeedVersion(db, datId: 8, fileId: 51, source, DatFileLifecycle.PendingActivation,
            importedAt: pendingImportedAt);
        SeedGameWithRom(db, gameId: 70, datFileId: 7, firstHashByte: 1);
        SeedGameWithRom(db, gameId: 80, datFileId: 8, firstHashByte: 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void SeedFile(RomdDbContext db, int fileId, DateTimeOffset createdAt) =>
        db.Files.Add(new FileEntityPersistence
        {
            Id = fileId,
            Sha256 = NewSha256((byte)fileId),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = createdAt
        });

    private static void SeedVersion(
        RomdDbContext db,
        int datId,
        int fileId,
        DatSourceEntity source,
        DatFileLifecycle lifecycle,
        DateTimeOffset importedAt,
        DateTimeOffset? supersededAt = null,
        int? platformId = null) =>
        db.DatFiles.Add(new DatFileEntity
        {
            Source = source,
            Id = datId,
            Name = $"DAT {datId}",
            Description = $"DAT {datId}",
            Type = nameof(DatType.NoIntro),
            OriginalFilename = $"dat-{datId}.dat",
            FileId = fileId,
            PlatformId = platformId,
            Lifecycle = lifecycle.ToString(),
            SupersededAt = supersededAt,
            CreatedAt = importedAt
        });

    private static void SeedGameWithRom(RomdDbContext db, int gameId, int datFileId, byte firstHashByte)
    {
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = 1,
            EntryKey = $"Game {gameId}",
            Name = $"Game {gameId}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = datFileId,
            SourceEntryId = gameId,
            Name = $"Game {gameId}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = gameId,
            DatGameId = gameId,
            Name = $"game-{gameId}.rom",
            Size = 1,
            Sha1 = NewSha1(firstHashByte),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static void SeedPlatform(RomdDbContext db, int platformId) =>
        db.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = $"Platform {platformId}",
            ShortName = $"p{platformId}",
            CreatedAt = DateTimeOffset.UtcNow
        });

    private static void SeedTitle(
        RomdDbContext db,
        int titleId,
        int platformId,
        DateTimeOffset createdAt,
        TitleCatalogState catalogState = TitleCatalogState.Active) =>
        db.Titles.Add(new TitleEntity
        {
            Id = titleId,
            PlatformId = platformId,
            Name = $"Title {titleId}",
            NormalizedName = $"title{titleId}",
            EnrichmentStatus = "None",
            CatalogState = catalogState,
            CreatedAt = createdAt
        });

    private static LibraryEntity Library(int id) => new()
    {
        Id = id,
        Name = $"Library {id}",
        ConfigurationJson = "{}",
        ConfigurationState = "Valid",
        NeedsMaterialization = false,
        CreatedAt = DateTimeOffset.UtcNow
    };

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
}
