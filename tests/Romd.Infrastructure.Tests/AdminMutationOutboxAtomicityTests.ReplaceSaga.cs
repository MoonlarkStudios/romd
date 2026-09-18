using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

/// <summary>
///     Real-PostgreSQL proofs for the #88 slice B replace saga: pending-ingest atomicity,
///     single-commit activate/supersede, duplicate-detection rescoping, retention, and the
///     one-Active-per-source invariant at every observable point.
/// </summary>
public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Fact]
    public async Task ActivateDatVersion_StaleReviewedBaseline_LeavesActiveGraphAndEventsUntouched()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedSourceWithActiveAndPendingAsync(database.Context);
        var handler = NewActivateHandler(database, database.UnitOfWork);
        var result = await handler.HandleAsync(ActivateDatVersionCommand.Create(8, expectedActiveDatId: 999).Value);
        result.FirstError.Code.ShouldBe("DatReview.Stale");
        await using var read = database.CreateReadContext();
        (await read.DatFiles.SingleAsync(d => d.Id == 7)).Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
        (await read.DatFiles.SingleAsync(d => d.Id == 8)).Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await read.DatGames.AnyAsync(g => g.DatFileId == 7)).ShouldBeTrue();
        (await GetEventTypesAsync(read)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceIngest_StaleReviewedBaseline_CreatesNoPendingVersionOrClaims()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        int claims = await database.Context.SourceEntries.CountAsync();
        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var command = IngestDatCommand.Create(new MemoryStream([1]), "replacement.dat", 10,
            sourceId, expectedActiveDatId: 999).Value;
        var result = await handler.HandleAsync(command);
        result.FirstError.Code.ShouldBe("DatReview.Stale");
        await using var read = database.CreateReadContext();
        (await read.DatFiles.CountAsync()).ShouldBe(1);
        (await read.SourceEntries.CountAsync()).ShouldBe(claims);
        (await read.DatFiles.SingleAsync()).Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
        (await GetEventTypesAsync(read)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceIngest_CommitOutcome_KeepsPendingVersionDirtyStateAndEventsAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var command = IngestDatCommand.Create(
            new MemoryStream([1]), "replacement.dat", platformId: 10, replacesDatSourceId: sourceId).Value;

        if (failCommit)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(command));
        }
        else
        {
            var result = await handler.HandleAsync(command);
            result.IsError.ShouldBeFalse();
            result.Value.DatFile.DatSourceId.ShouldBe(sourceId);
            result.Value.DatFile.Lifecycle.ShouldBe(DatFileLifecycle.PendingActivation);
        }

        await using var readContext = database.CreateReadContext();
        // No new source anchor either way: the replacement version rides the existing source.
        (await readContext.DatSources.AsNoTracking().CountAsync()).ShouldBe(1);
        (await readContext.DatFiles.AsNoTracking().CountAsync()).ShouldBe(failCommit ? 1 : 2);
        var oldVersion = await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 7);
        oldVersion.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));

        if (!failCommit)
        {
            var pending = await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id != 7);
            pending.DatSourceId.ShouldBe(sourceId);
            pending.Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        }

        // #135 adoption: the Dirty projection state is durable with the ingest commit even
        // though the post-commit rebuild acceleration never ran (suppressed here).
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(failCommit ? CatalogRebuildState.Clean : CatalogRebuildState.Dirty);
        (await GetEventTypesAsync(readContext)).ShouldBe(failCommit ? [] : StatsEventTypes());
    }

    [Fact]
    public async Task ReplaceIngest_CancelledAfterFinalFlush_RollsBackPendingVersionDirtyStateAndEvents()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        using var cancellation = new CancellationTokenSource();

        var handler = NewReplaceIngestHandler(
            database,
            new CancelAfterCommitFlushUnitOfWork(database.UnitOfWork, cancellation),
            storedFileId: 51);
        var command = IngestDatCommand.Create(
            new MemoryStream([1]), "replacement.dat", platformId: 10, replacesDatSourceId: sourceId).Value;

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token));

        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking().CountAsync()).ShouldBe(1);
        (await readContext.DatSources.AsNoTracking().CountAsync()).ShouldBe(1);
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivateDatVersion_CommitOutcome_KeepsActivationSupersedeGraphDeletionFlagsAndEventsAtomic(
        bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        int sourceId = await SeedSourceWithActiveAndPendingAsync(database.Context);
        await SeedLibraryAsync(database.Context, libraryId: 3);

        var handler = NewActivateHandler(database, database.UnitOfWork);
        var result = await handler.HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        result.IsError.ShouldBe(failCommit);
        await using var readContext = database.CreateReadContext();
        var oldVersion = await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 7);
        var newVersion = await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 8);
        var platform = await readContext.Platforms.SingleAsync(row => row.Id == 10);
        var library = await readContext.Libraries.SingleAsync(row => row.Id == 3);

        if (failCommit)
        {
            // Activation rollback leaves the old version Active and the new version Pending.
            oldVersion.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
            oldVersion.SupersededAt.ShouldBeNull();
            newVersion.Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
            (await readContext.DatGames.AsNoTracking().AnyAsync(row => row.DatFileId == 7)).ShouldBeTrue();
            (await readContext.DatRoms.AsNoTracking().AnyAsync(row => row.DatGameId == 70)).ShouldBeTrue();
            platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
            library.NeedsMaterialization.ShouldBeFalse();
            (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
        }
        else
        {
            result.Value.Lifecycle.ShouldBe(DatFileLifecycle.Active);
            newVersion.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
            oldVersion.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Superseded));
            oldVersion.SupersededAt.ShouldNotBeNull();
            // The superseded version's parsed graph is gone (children cascade); the new
            // version's graph and the provenance row itself are retained.
            (await readContext.DatGames.AsNoTracking().AnyAsync(row => row.DatFileId == 7)).ShouldBeFalse();
            (await readContext.DatRoms.AsNoTracking().AnyAsync(row => row.DatGameId == 70)).ShouldBeFalse();
            (await readContext.DatGames.AsNoTracking().AnyAsync(row => row.DatFileId == 8)).ShouldBeTrue();
            platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
            library.NeedsMaterialization.ShouldBeTrue();
            (await GetEventTypesAsync(readContext)).ShouldBe(StatsEventTypes());
        }

        (await readContext.DatFiles.AsNoTracking()
                .CountAsync(row => row.DatSourceId == sourceId
                                   && row.Lifecycle == nameof(DatFileLifecycle.Active)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task ActivateDatVersion_RetentionFailure_RollsBackEntireActivationMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedSourceWithActiveAndPendingAsync(database.Context);
        await SeedLibraryAsync(database.Context, libraryId: 3);
        var real = new DatRepository(database.Context, TimeProvider.System);
        var failing = Substitute.For<IDatRepository>();
        failing.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetByIdAsync(call.Arg<int>(), call.Arg<CancellationToken>()));
        failing.GetActiveBySourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetActiveBySourceIdAsync(call.Arg<int>(), call.Arg<CancellationToken>()));
        failing.GetRoutedPlatformIdsBySourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetRoutedPlatformIdsBySourceIdAsync(
                call.Arg<int>(), call.Arg<CancellationToken>()));
        failing.UpdateLifecycleStagedAsync(Arg.Any<DatFile>(), Arg.Any<CancellationToken>())
            .Returns(call => real.UpdateLifecycleStagedAsync(
                call.Arg<DatFile>(), call.Arg<CancellationToken>()));
        failing.DeleteGamesByDatFileIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.DeleteGamesByDatFileIdAsync(
                call.Arg<int>(), call.Arg<CancellationToken>()));
        failing.DeleteSupersededVersionsBeyondMostRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("retention failed"));

        var result = await NewActivateHandler(database, database.UnitOfWork, failing)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        result.IsError.ShouldBeTrue();
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.SingleAsync(file => file.Id == 7))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
        (await readContext.DatFiles.SingleAsync(file => file.Id == 8))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await readContext.DatGames.AnyAsync(game => game.DatFileId == 7)).ShouldBeTrue();
        (await readContext.Platforms.SingleAsync(platform => platform.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await readContext.Libraries.SingleAsync(library => library.Id == 3))
            .NeedsMaterialization.ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivateDatVersion_RetentionPrunesThirdPlatformClaim_InvalidationIsAtomic(
        bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        int sourceId = await SeedSourceWithActiveAndPendingAsync(database.Context);
        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 11,
            Name = "Historical Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Historical Platform", BaseCompactLabel = "Historical Platform", CanonicalKey = "historical", ShortName = "historical",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await SeedFileAsync(database.Context, fileId: 16, createdAt: DateTimeOffset.UtcNow);
        await SeedVersionAsync(
            database.Context,
            datId: 6,
            fileId: 16,
            sourceId,
            platformId: 11,
            lifecycle: DatFileLifecycle.Superseded);
        await SeedGameWithRomAsync(database.Context, gameId: 60, datFileId: 6, Sha1Three);
        database.Context.Titles.Add(new TitleEntity
        {
            Id = 91,
            PlatformId = 11,
            Name = "Historical Claim",
            NormalizedName = "historicalclaim",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        database.Context.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 60,
            TitleId = 91,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await SeedPlatformSelectiveLibraryAsync(database.Context, libraryId: 3, platformId: 10);
        await SeedPlatformSelectiveLibraryAsync(database.Context, libraryId: 4, platformId: 11);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        // Pin this as a real projected claim, not just an unused historical payload row.
        (await CatalogProjectionTestFactory.Create(database.Context)
                .RebuildPlatformAsync(11))
            .ShouldBeTrue();
        database.Context.ChangeTracker.Clear();
        (await database.Context.CatalogReleaseSources.AsNoTracking()
                .AnyAsync(source => source.SourceEntryId == 60))
            .ShouldBeTrue();

        var result = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        result.IsError.ShouldBe(failCommit);
        await using var readContext = database.CreateReadContext();
        foreach (int platformId in new[] { 10, 11 })
        {
            (await readContext.Platforms.SingleAsync(platform => platform.Id == platformId))
                .CatalogRebuildState.ShouldBe(
                    failCommit ? CatalogRebuildState.Clean : CatalogRebuildState.Dirty);
        }

        foreach (int libraryId in new[] { 3, 4 })
        {
            (await readContext.Libraries.SingleAsync(library => library.Id == libraryId))
                .NeedsMaterialization.ShouldBe(!failCommit);
        }

        // Retention, claim pruning, projection invalidation and library invalidation are one
        // outcome. A failed commit preserves the entire historical claim graph and projection.
        (await readContext.DatFiles.AnyAsync(file => file.Id == 6)).ShouldBe(failCommit);
        (await readContext.SourceEntries.AnyAsync(entry => entry.Id == 60)).ShouldBe(failCommit);
        (await readContext.CatalogReleaseSources.AnyAsync(source => source.SourceEntryId == 60))
            .ShouldBe(failCommit);
    }

    [Fact]
    public async Task ActivateDatVersion_EqualSupersededTimes_RetainsHigherIdWinner()
    {
        await using var database = await TestDatabase.CreateAsync();
        int sourceId = await SeedSourceWithActiveAndPendingAsync(database.Context);
        await SeedFileAsync(database.Context, fileId: 16, createdAt: DateTimeOffset.UtcNow);
        await SeedVersionAsync(
            database.Context, datId: 6, fileId: 16, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.Superseded);
        var fixedNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await database.Context.DatFiles
            .Where(file => file.Id == 6)
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.SupersededAt, fixedNow));

        var result = await NewActivateHandler(
                database,
                database.UnitOfWork,
                timeProvider: new ManualTimeProvider(fixedNow))
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        result.IsError.ShouldBeFalse();
        await using var readContext = database.CreateReadContext();
        var supersededIds = await readContext.DatFiles.AsNoTracking()
            .Where(file => file.DatSourceId == sourceId
                           && file.Lifecycle == nameof(DatFileLifecycle.Superseded))
            .Select(file => file.Id)
            .ToListAsync();
        supersededIds.ShouldBe([7]);
    }

    [Fact]
    public async Task ActivateDatVersion_CancelledAfterFinalFlush_RollsBackActivation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedSourceWithActiveAndPendingAsync(database.Context);
        await SeedLibraryAsync(database.Context, libraryId: 3);
        using var cancellation = new CancellationTokenSource();

        var handler = NewActivateHandler(
            database,
            new CancelAfterCommitFlushUnitOfWork(database.UnitOfWork, cancellation));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(ActivateDatVersionCommand.Create(8).Value, cancellation.Token));

        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 7))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
        (await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 8))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await readContext.DatGames.AsNoTracking().AnyAsync(row => row.DatFileId == 7)).ShouldBeTrue();
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        (await readContext.Libraries.SingleAsync(row => row.Id == 3)).NeedsMaterialization.ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ActivateDatVersion_AlreadyActive_NoOpsWithoutNewEffects()
    {
        await using var database = await TestDatabase.CreateAsync();
        int sourceId = await SeedSourceWithActiveAndPendingAsync(database.Context);
        var firstRun = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);
        firstRun.IsError.ShouldBeFalse();
        database.Context.ChangeTracker.Clear();

        // Redelivery after the committed activation converges without re-running the commit.
        var redelivery = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        redelivery.IsError.ShouldBeFalse();
        redelivery.Value.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking()
                .CountAsync(row => row.DatSourceId == sourceId
                                   && row.Lifecycle == nameof(DatFileLifecycle.Active)))
            .ShouldBe(1);
        (await GetEventTypesAsync(readContext)).ShouldBe(StatsEventTypes());
    }

    [Fact]
    public async Task ActivateDatVersion_StaleActiveLookupLoser_HitsActiveUniqueIndexAndErrsOpaquely()
    {
        await using var database = await TestDatabase.CreateAsync();
        int sourceId = await SeedSourceWithActiveAndPendingAsync(database.Context);

        // A loser whose Active lookup is stale tries to activate without superseding: the
        // partial unique Active index rejects the commit at the database.
        var real = new DatRepository(database.Context, TimeProvider.System);
        var stale = Substitute.For<IDatRepository>();
        stale.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetByIdAsync(call.Arg<int>(), call.Arg<CancellationToken>()));
        stale.GetActiveBySourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DatFile?>(null));
        stale.GetRoutedPlatformIdsBySourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetRoutedPlatformIdsBySourceIdAsync(
                call.Arg<int>(), call.Arg<CancellationToken>()));
        stale.UpdateLifecycleStagedAsync(Arg.Any<DatFile>(), Arg.Any<CancellationToken>())
            .Returns(call => real.UpdateLifecycleStagedAsync(call.Arg<DatFile>(), call.Arg<CancellationToken>()));

        var handler = NewActivateHandler(database, database.UnitOfWork, stale);
        var result = await handler.HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.DatabaseFailed");
        await using var readContext = database.CreateReadContext();
        var activeVersions = await readContext.DatFiles.AsNoTracking()
            .Where(row => row.DatSourceId == sourceId && row.Lifecycle == nameof(DatFileLifecycle.Active))
            .Select(row => row.Id)
            .ToListAsync();
        activeVersions.ShouldBe([7]);
        (await readContext.DatFiles.AsNoTracking().SingleAsync(row => row.Id == 8))
            .Lifecycle.ShouldBe(nameof(DatFileLifecycle.PendingActivation));
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceSaga_StateWalk_KeepsExactlyOneActiveAndRetainsNewestSuperseded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedGameWithRomAsync(database.Context, gameId: 70, datFileId: 7, Sha1One);
        var trackedAt = DateTimeOffset.UtcNow;
        database.Context.Titles.Add(new TitleEntity
        {
            Id = 90,
            PlatformId = 10,
            Name = "Tracked replacement title",
            NormalizedName = "trackedreplacementtitle",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = trackedAt
        });
        database.Context.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 70,
            TitleId = 90,
            CreatedAt = trackedAt
        });
        database.Context.TrackedTitles.Add(new TrackedTitleEntity
        {
            TitleId = 90,
            CreatedAt = trackedAt,
            UpdatedAt = trackedAt
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        await SeedFileAsync(database.Context, fileId: 52, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        var repository = new DatRepository(database.Context, TimeProvider.System);

        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 0, superseded: 0);

        // Round 1: ingest as pending; activation atomically retains the prior version.
        var ingested1 = await NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51)
            .HandleAsync(IngestDatCommand.Create(
                new MemoryStream([1]), "v2.dat", platformId: 10, replacesDatSourceId: sourceId).Value);
        ingested1.IsError.ShouldBeFalse();
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 1, superseded: 0);

        var activated1 = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(ingested1.Value.DatFile.Id).Value);
        activated1.IsError.ShouldBeFalse();
        database.Context.ChangeTracker.Clear();
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 0, superseded: 1);
        await using (var graphRead = database.CreateReadContext())
        {
            (await graphRead.DatGames.AsNoTracking().AnyAsync(row => row.DatFileId == 7)).ShouldBeFalse();
        }

        // The sweep/backstop remains idempotent at the retention limit.
        (await repository.DeleteSupersededVersionsBeyondMostRecentAsync(sourceId)).ShouldBe(0);
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 0, superseded: 1);

        // Exercise the actual post-activation catalog recovery after the old mapped graph is gone.
        (await CatalogProjectionTestFactory.Create(database.Context)
            .RebuildPlatformAsync(10)).ShouldBeTrue();

        // Round 2: activation supersedes v2 and drops the oldest version in the same commit.
        var ingested2 = await NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 52)
            .HandleAsync(IngestDatCommand.Create(
                new MemoryStream([2]), "v3.dat", platformId: 10, replacesDatSourceId: sourceId).Value);
        ingested2.IsError.ShouldBeFalse();
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 1, superseded: 1);

        var activated2 = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(ingested2.Value.DatFile.Id).Value);
        activated2.IsError.ShouldBeFalse();
        database.Context.ChangeTracker.Clear();
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 0, superseded: 1);

        await using var readContext = database.CreateReadContext();
        var retained = await readContext.DatFiles.AsNoTracking()
            .SingleAsync(row => row.Lifecycle == nameof(DatFileLifecycle.Superseded));
        retained.Id.ShouldBe(ingested1.Value.DatFile.Id);
        (await readContext.DatFiles.AsNoTracking()
                .SingleAsync(row => row.Lifecycle == nameof(DatFileLifecycle.Active)))
            .Id.ShouldBe(ingested2.Value.DatFile.Id);
        (await readContext.TrackedTitles.AsNoTracking().AnyAsync(row => row.TitleId == 90)).ShouldBeTrue();
        (await readContext.Titles.AsNoTracking().SingleAsync(row => row.Id == 90))
            .CatalogState.ShouldBe(TitleCatalogState.UserOnly);

        // Cleanup re-run is a no-op.
        (await repository.DeleteSupersededVersionsBeyondMostRecentAsync(sourceId)).ShouldBe(0);
        await AssertLifecycleCountsAsync(database, sourceId, active: 1, pending: 0, superseded: 1);
    }

    [Fact]
    public async Task ReplaceIngest_ContentMatchingRetainedSupersededVersion_ProceedsAsRollbackIngest()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            database.Context, datId: 8, fileId: 51, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.Superseded);

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var result = await handler.HandleAsync(IngestDatCommand.Create(
            new MemoryStream([1]), "rollback.dat", platformId: 10, replacesDatSourceId: sourceId).Value);

        // Re-uploading retained superseded content is the legitimate rollback path.
        result.IsError.ShouldBeFalse();
        result.Value.DatFile.Lifecycle.ShouldBe(DatFileLifecycle.PendingActivation);
        result.Value.DatFile.Id.ShouldNotBe(8);
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking().CountAsync(row => row.FileId == 51)).ShouldBe(2);
    }

    [Fact]
    public async Task ReplaceIngest_ContentMatchingActiveVersion_ReturnsDatAlreadyExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 17);
        var result = await handler.HandleAsync(IngestDatCommand.Create(
            new MemoryStream([1]), "same.dat", platformId: 10, replacesDatSourceId: sourceId).Value);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.DatAlreadyExists");
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking().CountAsync()).ShouldBe(1);
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplaceIngest_ContentMatchingOwnPendingVersion_ReturnsExistingWithoutReingesting()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            database.Context, datId: 8, fileId: 51, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.PendingActivation);
        await SeedGameWithRomAsync(database.Context, gameId: 80, datFileId: 8, Sha1Two);

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var result = await handler.HandleAsync(IngestDatCommand.Create(
            new MemoryStream([1]), "redelivered.dat", platformId: 10, replacesDatSourceId: sourceId).Value);

        // A redelivered saga finds its committed pending version and skips forward.
        result.IsError.ShouldBeFalse();
        result.Value.DatFile.Id.ShouldBe(8);
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.AsNoTracking().CountAsync()).ShouldBe(2);
        (await readContext.DatGames.AsNoTracking().CountAsync(row => row.DatFileId == 8)).ShouldBe(1);
        (await GetEventTypesAsync(readContext)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FreshIngest_ContentMatchingPendingVersion_ReturnsDatAlreadyExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            database.Context, datId: 8, fileId: 51, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.PendingActivation);

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var result = await handler.HandleAsync(IngestDatCommand.Create(
            new MemoryStream([1]), "fresh.dat", platformId: 10).Value);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.DatAlreadyExists");
    }

    [Fact]
    public async Task FreshIngest_ContentMatchingSupersededVersion_ProceedsWithNewSource()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            database.Context, datId: 8, fileId: 51, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.Superseded);

        var handler = NewReplaceIngestHandler(database, database.UnitOfWork, storedFileId: 51);
        var result = await handler.HandleAsync(IngestDatCommand.Create(
            new MemoryStream([1]), "fresh.dat", platformId: 10).Value);

        // Retained provenance never trips duplicate detection for fresh uploads either.
        result.IsError.ShouldBeFalse();
        result.Value.DatFile.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        result.Value.DatFile.DatSourceId.ShouldNotBe(sourceId);
        await using var readContext = database.CreateReadContext();
        (await readContext.DatSources.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    private static async Task AssertLifecycleCountsAsync(
        TestDatabase database,
        int sourceId,
        int active,
        int pending,
        int superseded)
    {
        await using var readContext = database.CreateReadContext();
        var counts = await readContext.DatFiles.AsNoTracking()
            .Where(row => row.DatSourceId == sourceId)
            .GroupBy(row => row.Lifecycle)
            .Select(group => new { Lifecycle = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Lifecycle, group => group.Count);

        counts.GetValueOrDefault(nameof(DatFileLifecycle.Active)).ShouldBe(active);
        counts.GetValueOrDefault(nameof(DatFileLifecycle.PendingActivation)).ShouldBe(pending);
        counts.GetValueOrDefault(nameof(DatFileLifecycle.Superseded)).ShouldBe(superseded);
    }

    [Fact]
    public async Task ActivateDatVersion_PlatformOverride_MarksBothPlatformsDirty()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedGameWithRomAsync(database.Context, gameId: 70, datFileId: 7, Sha1One);
        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 11,
            Name = "Override Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Override Platform", BaseCompactLabel = "Override Platform", CanonicalKey = "override", ShortName = "override",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await database.Context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            database.Context, datId: 8, fileId: 51, sourceId, platformId: 11,
            lifecycle: DatFileLifecycle.PendingActivation);
        await SeedGameWithRomAsync(database.Context, gameId: 80, datFileId: 8, Sha1Two);

        var result = await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value);

        // A replace that overrides the platform leaves BOTH projections stale: the old
        // platform lost its games, the new platform gained them.
        result.IsError.ShouldBeFalse();
        await using var readContext = database.CreateReadContext();
        (await readContext.Platforms.SingleAsync(row => row.Id == 10))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await readContext.Platforms.SingleAsync(row => row.Id == 11))
            .CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
    }

    private static async Task<int> SeedSourceWithActiveAndPendingAsync(RomdDbContext context)
    {
        await SeedPlatformFileAndDatAsync(context, datId: 7, fileId: 17, platformId: 10);
        await SeedGameWithRomAsync(context, gameId: 70, datFileId: 7, Sha1One);
        await SeedFileAsync(context, fileId: 51, createdAt: DateTimeOffset.UtcNow);
        int sourceId = (await context.DatSources.AsNoTracking().SingleAsync()).Id;
        await SeedVersionAsync(
            context, datId: 8, fileId: 51, sourceId, platformId: 10,
            lifecycle: DatFileLifecycle.PendingActivation);
        await SeedGameWithRomAsync(context, gameId: 80, datFileId: 8, Sha1Two);
        return sourceId;
    }

    private static async Task SeedVersionAsync(
        RomdDbContext context,
        int datId,
        int fileId,
        int sourceId,
        int platformId,
        DatFileLifecycle lifecycle)
    {
        context.DatFiles.Add(new DatFileEntity
        {
            Id = datId,
            Name = $"Version DAT {datId}",
            Description = $"Version DAT {datId}",
            Type = DatType.NoIntro.ToString(),
            PlatformId = platformId,
            OriginalFilename = $"version-{datId}.dat",
            FileId = fileId,
            DatSourceId = sourceId,
            Lifecycle = lifecycle.ToString(),
            SupersededAt = lifecycle == DatFileLifecycle.Superseded ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedPlatformSelectiveLibraryAsync(
        RomdDbContext context,
        int libraryId,
        int platformId)
    {
        context.Libraries.Add(new LibraryEntity
        {
            Id = libraryId,
            Name = $"Platform {platformId} Library",
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration
            {
                AllowedPlatformIds = [platformId]
            }),
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedGameWithRomAsync(
        RomdDbContext context,
        int gameId,
        int datFileId,
        Sha1 sha1)
    {
        int catalogSourceId = await context.DatFiles
            .AsNoTracking()
            .Where(dat => dat.Id == datFileId)
            .Join(context.DatSources, dat => dat.DatSourceId, source => source.Id, (_, source) => source.CatalogSourceId)
            .SingleAsync();
        context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = catalogSourceId,
            EntryKey = $"Game {gameId}",
            Name = $"Game {gameId}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = datFileId,
            SourceEntryId = gameId,
            Name = $"Game {gameId}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatRoms.Add(new DatRomEntity
        {
            Id = gameId * 10,
            DatGameId = gameId,
            Name = $"game-{gameId}.rom",
            Size = 1,
            Sha1 = sha1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static ActivateDatVersionCommandHandler NewActivateHandler(
        TestDatabase database,
        IUnitOfWork unitOfWork,
        IDatRepository? repository = null,
        TimeProvider? timeProvider = null) =>
        new(
            repository ?? new DatRepository(database.Context, TimeProvider.System),
            CatalogProjectionTestFactory.Create(database.Context),
            new LibraryRepository(database.Context),
            database.Outbox,
            unitOfWork,
            timeProvider ?? TimeProvider.System,
            NullLogger<ActivateDatVersionCommandHandler>.Instance,
            new SourceLifecycleStore(database.Context));

    private static IngestDatCommandHandler NewReplaceIngestHandler(
        TestDatabase database,
        IUnitOfWork unitOfWork,
        int storedFileId) => NewReplaceIngestHandler(database.Context, unitOfWork, storedFileId);

    private static IngestDatCommandHandler NewReplaceIngestHandler(
        RomdDbContext context, IUnitOfWork unitOfWork, int storedFileId,
        ILogger<IngestDatCommandHandler>? logger = null)
    {
        var tempFile = new AtomicityTempFile([1], NewSha256(storedFileId + 100));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(tempFile);
        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.StoreFromTempFileAsync(tempFile, Arg.Any<CancellationToken>())
            .Returns(new FileStoreResult(
                FileEntity.Rehydrate(storedFileId, NewSha256(storedFileId), 1, 1, false, DateTimeOffset.UtcNow),
                false,
                true));
        var datReader = Substitute.For<IDatReader>();
        datReader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatMetadata
            {
                Name = "Replacement DAT",
                Description = "Replacement DAT",
                DatType = DatType.NoIntro
            });
        datReader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyDatGameStream());
        var regionResolver = Substitute.For<ITaxonomyResolver<Region>>();
        regionResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var languageResolver = Substitute.For<ITaxonomyResolver<GameLanguage>>();
        languageResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var headerResolver = Substitute.For<IPlatformHeaderResolver>();
        headerResolver.ResolvePlatformIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var datRepository = new DatRepository(context, TimeProvider.System);
        return new IngestDatCommandHandler(
            datReader,
            datRepository,
            new TitleDerivationService(context, new TitleMatcher(new TitleRepository(context))),
            tempFiles,
            fileStorage,
            Substitute.For<IBiosGrouper>(),
            unitOfWork,
            new AdminRealtimeOutbox(context, TimeProvider.System),
            Substitute.For<ILibraryRepository>(),
            regionResolver,
            languageResolver,
            new RebuildSuppressingCatalogProjection(CatalogProjectionTestFactory.Create(context)),
            headerResolver,
            logger ?? NullLogger<IngestDatCommandHandler>.Instance,
            new SourceLifecycleStore(context));
    }

    /// <summary>
    ///     Forwards the in-commit Dirty marking to the real projection service while
    ///     suppressing the post-commit rebuild acceleration, so tests observe exactly the
    ///     durable state a crash between commit and rebuild would leave behind.
    /// </summary>
    private sealed class RebuildSuppressingCatalogProjection(ICatalogProjectionService inner)
        : ICatalogProjectionService
    {
        public Task<bool> RebuildPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task MarkPlatformDirtyAsync(
            int platformId,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<int>? affectedTitleIds = null) =>
            inner.MarkPlatformDirtyAsync(platformId, cancellationToken, affectedTitleIds);

        public Task MarkCatalogSourceDirtyAsync(
            int platformId,
            int catalogSourceId,
            CancellationToken cancellationToken = default) =>
            inner.MarkCatalogSourceDirtyAsync(platformId, catalogSourceId, cancellationToken);

        public Task RefreshCatalogSourcePayloadAsync(
            int catalogSourceId,
            CancellationToken cancellationToken = default) =>
            inner.RefreshCatalogSourcePayloadAsync(catalogSourceId, cancellationToken);

        public Task RefreshPlatformPayloadAsync(
            int platformId,
            CancellationToken cancellationToken = default) =>
            inner.RefreshPlatformPayloadAsync(platformId, cancellationToken);

        public Task<IReadOnlyList<int>> GetPlatformIdsNeedingRebuildAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetPlatformIdsNeedingRebuildAsync(cancellationToken);
    }
}
