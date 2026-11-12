using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public sealed class TaxonomyMergeTests
{
    [Fact]
    public async Task Merge_RegisteredReferenceIdentity_CannotBeReassigned()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var source = new RegionEntity { Name = "Registered", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Registered", CanonicalKey = "registered" };
        var target = new RegionEntity { Name = "Other", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Other") };
        db.Regions.AddRange(source, target);
        await db.SaveChangesAsync();
        var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
        (await service.MergeAsync(source.CanonicalKey, target.CanonicalKey, default)).FirstError.Code.ShouldBe("Taxonomy.ReferenceIdentity");
        (await service.PreviewMergeAsync(source.CanonicalKey, target.CanonicalKey, default)).FirstError.Code.ShouldBe("Taxonomy.ReferenceIdentity");
        (await db.Regions.AnyAsync(row => row.Id == source.Id && row.CanonicalKey == "registered")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_PreservesCanonicalReleaseMemberships_DeduplicatesAndRecordsEvidence(bool regions)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var platform = new PlatformEntity { Name = "Merge test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Merge test", BaseCompactLabel = "Merge test", CanonicalKey = "merge", ShortName = "merge" };
        db.Platforms.Add(platform);
        await db.SaveChangesAsync();
        var title = new TitleEntity { PlatformId = platform.Id, Name = "Test", NormalizedName = "test", EnrichmentStatus = "Pending" };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        var releases = Enumerable.Range(0, 2).Select(index => new CatalogReleaseEntity
        {
            PlatformId = platform.Id,
            CatalogTitleId = title.Id,
            Name = $"Release {index}",
            Fingerprint = $"test-{index}"
        }).ToArray();
        db.CatalogReleases.AddRange(releases);
        await db.SaveChangesAsync();
        if (regions)
        {
            var source = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
            var target = new RegionEntity { Name = "Target", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
            db.Regions.AddRange(source, target);
            await db.SaveChangesAsync();
            db.CatalogReleaseRegions.AddRange(releases.Select(release => new CatalogReleaseRegionEntity { CatalogReleaseId = release.Id, RegionId = source.Id }));
            db.CatalogReleaseRegions.Add(new CatalogReleaseRegionEntity { CatalogReleaseId = releases[0].Id, RegionId = target.Id });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
            (await service.MergeAsync(source.CanonicalKey, target.CanonicalKey, default)).IsError.ShouldBeFalse();
            (await db.CatalogReleaseRegions.CountAsync(item => item.RegionId == target.Id)).ShouldBe(2);
            (await db.Regions.AnyAsync(item => item.Id == source.Id)).ShouldBeFalse();
        }
        else
        {
            var source = new GameLanguageEntity { Name = "Source", Code = "SRC", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
            var target = new GameLanguageEntity { Name = "Target", Code = "DST", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
            db.GameLanguages.AddRange(source, target);
            await db.SaveChangesAsync();
            db.CatalogReleaseLanguages.AddRange(releases.Select(release => new CatalogReleaseLanguageEntity { CatalogReleaseId = release.Id, GameLanguageId = source.Id }));
            db.CatalogReleaseLanguages.Add(new CatalogReleaseLanguageEntity { CatalogReleaseId = releases[0].Id, GameLanguageId = target.Id });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var service = new TaxonomyService<GameLanguage>(new LanguageRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<GameLanguage>>.Instance);
            (await service.MergeAsync(source.CanonicalKey, target.CanonicalKey, default)).IsError.ShouldBeFalse();
            (await db.CatalogReleaseLanguages.CountAsync(item => item.GameLanguageId == target.Id)).ShouldBe(2);
            (await db.GameLanguages.AnyAsync(item => item.Id == source.Id)).ShouldBeFalse();
        }
        // The audit names public keys, never internal ids.
        var audit = await db.AdminAuditEvents.SingleAsync(item => item.Action == "Merged");
        audit.TargetId.ShouldBe("dat-source");
        audit.Changes.ShouldBe("{\"TargetKey\":\"dat-target\"}");
    }

    [Fact]
    public async Task Merge_KeyedRefusals_ComeBeforeStateAndNameNoInternalIds()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var source = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        db.Regions.Add(source);
        await db.SaveChangesAsync();
        var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);

        (await service.MergeAsync("dat-source", "dat-source", default)).FirstError.Code.ShouldBe("Taxonomy.MergeIntoSelf");
        // The path's source is resolved before the body's target.
        (await service.MergeAsync("dat-missing", "dat-unknown", default)).FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        (await service.MergeAsync("dat-source", "dat-unknown", default)).FirstError.Code.ShouldBe("Taxonomy.UnknownTarget");
        var missingSource = (await service.PreviewMergeAsync("dat-missing", "dat-source", default)).FirstError;
        missingSource.Code.ShouldBe("ReferenceResource.NotFound");
        missingSource.Description.ShouldNotContain(source.Id.ToString());
        (await service.AddAliasAsync("dat-source", new string('a', 101), default)).FirstError.Code.ShouldBe("Taxonomy.InvalidAlias");
        (await service.AddAliasAsync("dat-source", " \t ", default)).FirstError.Code.ShouldBe("Taxonomy.InvalidAlias");
        (await service.AddAliasAsync("dat-source", "bad\u0007alias", default)).FirstError.Code.ShouldBe("Taxonomy.InvalidAlias");
        (await service.AddAliasAsync("dat-missing", "fine", default)).FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        (await service.RemoveAliasAsync("dat-source", int.MaxValue, default)).FirstError.Code.ShouldBe("Taxonomy.AliasNotFound");
    }

    [Fact]
    public async Task Merge_ConcurrentMergesOfOneSource_OneMergesAndTheOtherFindsNoSource()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database);

        // Holding the source row lets both merges resolve it, then wait for it inside their transactions.
        await using var hold = await RegionRowHold.AcquireAsync(database, source.Id);
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (firstMerge, firstMergeWaited) = await database.StartWaitingForALockAsync(first,
            ct => Service(first).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        var (secondMerge, secondMergeWaited) = await database.StartWaitingForALockAsync(second,
            ct => Service(second).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        await hold.ReleaseAsync();
        var results = await Task.WhenAll(firstMerge, secondMerge).WaitAsync(TimeSpan.FromSeconds(30));

        firstMergeWaited.ShouldBeTrue();
        secondMergeWaited.ShouldBeTrue();
        results.Count(result => !result.IsError).ShouldBe(1);
        results.Single(result => result.IsError).FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        await using var check = database.CreateContext();
        (await check.Regions.Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-target"]);
    }

    [Fact]
    public async Task Merge_IntoARegisteredTarget_LeadsLaterImportsOfTheTokenToTheTarget()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        int created;
        await using (var ingest = await db.Database.BeginTransactionAsync())
        {
            created = (await Resolver(db).ResolveAsync("Atlantis")).ShouldHaveSingleItem();
            await ingest.CommitAsync();
        }
        string minted = (await db.Regions.SingleAsync(row => row.Id == created)).CanonicalKey;

        (await Service(db).MergeAsync(minted, "asia", default)).IsError.ShouldBeFalse();

        int asia = await db.Regions.Where(row => row.CanonicalKey == "asia").Select(row => row.Id).SingleAsync();
        (await Resolver(db).ResolveAsync("ATLANTIS")).ShouldHaveSingleItem().ShouldBe(asia);
        (await db.Regions.AnyAsync(row => row.CanonicalKey == minted)).ShouldBeFalse();
    }

    [Fact]
    public async Task AddAlias_QueuedBehindAMergeOfItsRow_IsNotFoundAndAddsNothing()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database);
        await using var hold = await RegionRowHold.AcquireAsync(database, source.Id);
        await using var mergeDb = AuditedContext(database);
        await using var addDb = AuditedContext(database);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => Service(mergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        var (add, addWaited) = await database.StartWaitingForALockAsync(addDb,
            ct => Service(addDb).AddAliasAsync("dat-source", "Atlantis", ct), timeout.Token);
        await hold.ReleaseAsync();

        mergeWaited.ShouldBeTrue();
        addWaited.ShouldBeTrue();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        (await add.WaitAsync(TimeSpan.FromSeconds(30))).FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        await using var check = database.CreateContext();
        (await check.RegionAliases.AnyAsync(alias => alias.NormalizedAlias == "atlantis")).ShouldBeFalse();
    }

    [Fact]
    public async Task AddAlias_AheadOfAMergeOfItsRow_CommitsAndTheMergeCarriesItToTheTarget()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database);
        await using var hold = await RegionRowHold.AcquireAsync(database, source.Id);
        await using var addDb = AuditedContext(database);
        await using var mergeDb = AuditedContext(database);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (add, addWaited) = await database.StartWaitingForALockAsync(addDb,
            ct => Service(addDb).AddAliasAsync("dat-source", "Atlantis", ct), timeout.Token);
        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => Service(mergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        await hold.ReleaseAsync();

        addWaited.ShouldBeTrue();
        mergeWaited.ShouldBeTrue();
        (await add.WaitAsync(TimeSpan.FromSeconds(30))).Value.Alias.ShouldBe("atlantis");
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        await using var check = database.CreateContext();
        (await check.RegionAliases.SingleAsync(alias => alias.NormalizedAlias == "atlantis")).RegionId.ShouldBe(target.Id);
        (await check.AdminAuditEvents.Where(item => item.Changes.Contains("NormalizedAlias"))
                .Select(item => new { item.TargetType, item.TargetId, item.Action }).SingleAsync())
            .ShouldBe(new { TargetType = "Region", TargetId = "dat-source", Action = "Added" });
    }

    [Fact]
    public async Task RemoveAlias_QueuedBehindAMergeOfItsRow_IsNotFoundAndTheAliasMovesWithTheMerge()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database);
        var alias = new RegionAliasEntity { RegionId = source.Id, NormalizedAlias = "atlantis" };
        await using (var db = database.CreateContext())
        {
            db.RegionAliases.Add(alias);
            await db.SaveChangesAsync();
        }

        await using var hold = await RegionRowHold.AcquireAsync(database, source.Id);
        await using var mergeDb = AuditedContext(database);
        await using var removeDb = AuditedContext(database);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => Service(mergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        var (remove, removeWaited) = await database.StartWaitingForALockAsync(removeDb,
            ct => Service(removeDb).RemoveAliasAsync("dat-source", alias.Id, ct), timeout.Token);
        await hold.ReleaseAsync();

        mergeWaited.ShouldBeTrue();
        removeWaited.ShouldBeTrue();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        (await remove.WaitAsync(TimeSpan.FromSeconds(30))).FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        await using var check = database.CreateContext();
        (await check.RegionAliases.SingleAsync(row => row.Id == alias.Id)).RegionId.ShouldBe(target.Id);
    }

    // A context that audits its writes as the admin host does, so an alias write names its row by key.
    private static RomdDbContext AuditedContext(PostgreSqlTestDatabase database, params IInterceptor[] observers)
    {
        var actor = Substitute.For<IAuditContext>();
        actor.ActorId.Returns(Guid.NewGuid());
        return new RomdDbContext(database.CreateOptions(options =>
            options.AddInterceptors([new AuditInterceptor(actor, TimeProvider.System), .. observers])));
    }

    [Fact]
    public async Task AddAlias_WhileAnIngestReferencesTheRow_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedSourceAndTargetAsync(database);
        await using var ingest = await IngestLinkHold.AcquireAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var addDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var added = await Service(addDb).AddAliasAsync("dat-target", "Atlantis", timeout.Token);

        added.Value.Alias.ShouldBe("atlantis");
    }

    [Fact]
    public async Task RemoveAlias_WhileAnIngestReferencesTheRow_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedSourceAndTargetAsync(database);
        int aliasId = await SeedAliasAsync(database, target.Id, "atlantis");
        await using var ingest = await IngestLinkHold.AcquireAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var removeDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var removed = await Service(removeDb).RemoveAliasAsync("dat-target", aliasId, timeout.Token);

        removed.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Merge_IntoARowAnIngestReferences_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedSourceAndTargetAsync(database);
        await using var ingest = await IngestLinkHold.AcquireAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var mergeDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var merged = await Service(mergeDb).MergeAsync("dat-source", "dat-target", timeout.Token);

        merged.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Merge_OfARowAnIngestReferences_WaitsForTheIngestThenMovesItsLink()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database);
        int game = await SeedDatGameAsync(database);
        await using var ingest = await IngestLinkHold.AcquireAsync(database, game, source.Id);
        await using var mergeDb = AuditedContext(database);

        // The merge deletes its source, so it waits for every transaction that references it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => Service(mergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        await ingest.CommitAsync();

        mergeWaited.ShouldBeTrue();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        await using var check = database.CreateContext();
        (await check.DatGameRegions.Select(link => new { link.DatGameId, link.RegionId }).SingleAsync())
            .ShouldBe(new { DatGameId = game, RegionId = target.Id });
    }

    [Fact]
    public async Task Merge_OfARowAnIngestHoldsPastTheLockTimeout_Is409AndChangesNothing()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database);
        int aliasId = await SeedAliasAsync(database, source.Id, "atlantis");
        int game = await SeedDatGameAsync(database);
        // The ingest holds the source FOR KEY SHARE for longer than the merge's lock timeout.
        await using var ingest = await IngestLinkHold.AcquireAsync(database, game, source.Id);
        var log = new CommandLog();
        await using var mergeDb = AuditedContext(database, log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var merged = await Service(mergeDb).MergeAsync("dat-source", "dat-target", timeout.Token);

        merged.FirstError.Code.ShouldBe("Taxonomy.MergeConflict");
        // A lock timeout is not retried: the ingest it waited for still runs.
        log.Commands.Count(command => command.Contains("FOR UPDATE")).ShouldBe(1);
        await ingest.CommitAsync();
        await using var check = database.CreateContext();
        (await check.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-source", "dat-target"]);
        (await check.RegionAliases.SingleAsync(alias => alias.Id == aliasId)).RegionId.ShouldBe(source.Id);
        (await check.DatGameRegions.SingleAsync()).RegionId.ShouldBe(source.Id);
        (await check.AdminAuditEvents.AnyAsync(item => item.Action == "Merged")).ShouldBeFalse();
    }

    // Languages take their row locks with their own statements, so each region lock-mode test has a language twin.
    [Fact]
    public async Task AddLanguageAlias_WhileAnIngestReferencesTheRow_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedLanguageSourceAndTargetAsync(database);
        await using var ingest = await IngestLinkHold.AcquireLanguageAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var addDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var added = await LanguageService(addDb).AddAliasAsync("dat-target", "Klingon", timeout.Token);

        added.Value.Alias.ShouldBe("klingon");
    }

    [Fact]
    public async Task RemoveLanguageAlias_WhileAnIngestReferencesTheRow_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedLanguageSourceAndTargetAsync(database);
        int aliasId;
        await using (var db = database.CreateContext())
        {
            var alias = new GameLanguageAliasEntity { GameLanguageId = target.Id, NormalizedAlias = "klingon" };
            db.GameLanguageAliases.Add(alias);
            await db.SaveChangesAsync();
            aliasId = alias.Id;
        }
        await using var ingest = await IngestLinkHold.AcquireLanguageAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var removeDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var removed = await LanguageService(removeDb).RemoveAliasAsync("dat-target", aliasId, timeout.Token);

        removed.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeLanguage_IntoARowAnIngestReferences_DoesNotWaitForTheIngest()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedLanguageSourceAndTargetAsync(database);
        await using var ingest = await IngestLinkHold.AcquireLanguageAsync(database, await SeedDatGameAsync(database), target.Id);
        await using var mergeDb = AuditedContext(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var merged = await LanguageService(mergeDb).MergeAsync("dat-source", "dat-target", timeout.Token);

        merged.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task MergeLanguage_OfARowAnIngestReferences_WaitsForTheIngestThenMovesItsLink()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedLanguageSourceAndTargetAsync(database);
        int game = await SeedDatGameAsync(database);
        await using var ingest = await IngestLinkHold.AcquireLanguageAsync(database, game, source.Id);
        await using var mergeDb = AuditedContext(database);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => LanguageService(mergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        await ingest.CommitAsync();

        mergeWaited.ShouldBeTrue();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        await using var check = database.CreateContext();
        (await check.DatGameLanguages.Select(link => new { link.DatGameId, link.GameLanguageId }).SingleAsync())
            .ShouldBe(new { DatGameId = game, GameLanguageId = target.Id });
    }

    [Fact]
    public async Task Merge_DeadlockedWithACatalogRebuild_RetriesAndMovesTheRebuiltLink()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database);
        int release = await SeedCatalogReleaseAsync(database);
        await using (var db = database.CreateContext())
        {
            db.CatalogReleaseRegions.Add(new CatalogReleaseRegionEntity { CatalogReleaseId = release, RegionId = source.Id });
            await db.SaveChangesAsync();
        }

        await using var rebuild = await CatalogRebuildHold.DeleteLinksAsync(database, release);
        var log = new CommandLog();
        var gate = new CatalogRebuildMergeGate();
        await using var mergeDb = AuditedContext(database, log, gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int mergeProcess = await mergeDb.OpenBackendProcessIdAsync(timeout.Token);
        await mergeDb.Database.ExecuteSqlRawAsync("SET deadlock_timeout = '1s'");

        var merge = Service(mergeDb).MergeAsync("dat-source", "dat-target", timeout.Token);
        Task? insert = null;
        Exception? failure = null;
        try
        {
            // Queue the rebuild's foreign-key check behind the locked source before the merge can wait for the
            // deleted link. The cycle is established by observed locks, not by beating a deadlock timer.
            await gate.SourceLocked.Task.WaitAsync(timeout.Token);
            insert = rebuild.InsertLinkAsync(release, source.Id, timeout.Token);
            (await database.WaitsForALockAsync(rebuild.ProcessId, insert, timeout.Token)).ShouldBeTrue();
            gate.ContinueFirstAttempt.SetResult();

            // Let the rebuild finish acquiring its source lock before the retry competes for that row again.
            // PostgreSQL does not promise that releasing a deadlock victim schedules the rebuild first.
            await insert.WaitAsync(TimeSpan.FromSeconds(30));
            await gate.RetryStarting.Task.WaitAsync(timeout.Token);
            gate.ContinueRetry.SetResult();
            // The rebuild now references the source, so the merge's retry waits for the rebuild to commit.
            (await database.WaitsForALockAsync(mergeProcess, merge, timeout.Token)).ShouldBeTrue();
            await rebuild.CommitAsync();

            (await merge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
            gate.Deadlocks.ShouldBe(1);
            log.Commands.Count(command => command.Contains("FOR UPDATE")).ShouldBe(2);
            await using var check = database.CreateContext();
            (await check.CatalogReleaseRegions.Select(link => new { link.CatalogReleaseId, link.RegionId }).SingleAsync())
                .ShouldBe(new { CatalogReleaseId = release, RegionId = target.Id });
            (await check.Regions.AnyAsync(row => row.Id == source.Id)).ShouldBeFalse();
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, failure, merge, insert ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task Merge_TwoSourcesIntoOneTarget_SerializeAndKeepOneLinkPerGame()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var first = new RegionEntity { Name = "First", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("First") };
        var second = new RegionEntity { Name = "Second", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Second") };
        var target = new RegionEntity { Name = "Target", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        int game = await SeedDatGameAsync(database);
        await using (var db = database.CreateContext())
        {
            db.Regions.AddRange(first, second, target);
            await db.SaveChangesAsync();
            db.DatGameRegions.AddRange(
                new DatGameRegionEntity { DatGameId = game, RegionId = first.Id },
                new DatGameRegionEntity { DatGameId = game, RegionId = second.Id });
            await db.SaveChangesAsync();
        }

        // Both merges move the game's link onto the target, so they must take turns on the target row.
        await using var hold = await RegionRowHold.AcquireAsync(database, target.Id);
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (firstMerge, firstMergeWaited) = await database.StartWaitingForALockAsync(firstDb,
            ct => Service(firstDb).MergeAsync("dat-first", "dat-target", ct), timeout.Token);
        var (secondMerge, secondMergeWaited) = await database.StartWaitingForALockAsync(secondDb,
            ct => Service(secondDb).MergeAsync("dat-second", "dat-target", ct), timeout.Token);
        await hold.ReleaseAsync();

        firstMergeWaited.ShouldBeTrue();
        secondMergeWaited.ShouldBeTrue();
        (await firstMerge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        (await secondMerge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        await using var check = database.CreateContext();
        (await check.DatGameRegions.Select(link => new { link.DatGameId, link.RegionId }).SingleAsync())
            .ShouldBe(new { DatGameId = game, RegionId = target.Id });
    }

    [Fact]
    public async Task RemoveAlias_TwoConcurrentRemovesOfOneAlias_OneRemovesAndTheOtherFindsNoAlias()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedSourceAndTargetAsync(database);
        int aliasId = await SeedAliasAsync(database, target.Id, "atlantis");
        await using var hold = await RegionRowHold.AcquireAsync(database, target.Id);
        await using var firstDb = AuditedContext(database);
        await using var secondDb = AuditedContext(database);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (firstRemove, firstRemoveWaited) = await database.StartWaitingForALockAsync(firstDb,
            ct => Service(firstDb).RemoveAliasAsync("dat-target", aliasId, ct), timeout.Token);
        var (secondRemove, secondRemoveWaited) = await database.StartWaitingForALockAsync(secondDb,
            ct => Service(secondDb).RemoveAliasAsync("dat-target", aliasId, ct), timeout.Token);
        await hold.ReleaseAsync();

        firstRemoveWaited.ShouldBeTrue();
        secondRemoveWaited.ShouldBeTrue();
        (await firstRemove.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        (await secondRemove.WaitAsync(TimeSpan.FromSeconds(30))).FirstError.Code.ShouldBe("Taxonomy.AliasNotFound");
    }

    [Fact]
    public async Task RemoveAlias_AnotherRowsAliasThroughThisRowsUrl_IsAliasNotFoundAndKeepsTheAlias()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database);
        int aliasId = await SeedAliasAsync(database, source.Id, "atlantis");
        await using var db = AuditedContext(database);

        var removed = await Service(db).RemoveAliasAsync("dat-target", aliasId, default);

        removed.FirstError.Code.ShouldBe("Taxonomy.AliasNotFound");
        await using var check = database.CreateContext();
        (await check.RegionAliases.SingleAsync(alias => alias.Id == aliasId)).RegionId.ShouldBe(source.Id);
    }

    [Fact]
    public async Task RemoveAlias_MovedToTheMergeTarget_ThroughTheSourceUrlIsNotFoundAndStaysOnTheTarget()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database);
        int aliasId = await SeedAliasAsync(database, source.Id, "atlantis");
        await using var db = AuditedContext(database);
        (await Service(db).MergeAsync("dat-source", "dat-target", default)).IsError.ShouldBeFalse();

        var removed = await Service(db).RemoveAliasAsync("dat-source", aliasId, default);

        removed.FirstError.Code.ShouldBe("ReferenceResource.NotFound");
        await using var check = database.CreateContext();
        (await check.RegionAliases.SingleAsync(alias => alias.Id == aliasId)).RegionId.ShouldBe(target.Id);
    }

    [Fact]
    public async Task AddAlias_ADuplicateCommittedWhileItWaits_Is409AndSendsNothingAfterTheFailedInsert()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database);
        // Another writer has inserted the alias but not committed it, so the add's pre-check cannot see it.
        await using var other = database.CreateContext();
        await using var otherTransaction = await other.Database.BeginTransactionAsync();
        other.RegionAliases.Add(new RegionAliasEntity { RegionId = source.Id, NormalizedAlias = "atlantis" });
        await other.SaveChangesAsync();
        var log = new CommandLog();
        await using var addDb = AuditedContext(database, log);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (add, addWaited) = await database.StartWaitingForALockAsync(addDb,
            ct => Service(addDb).AddAliasAsync("dat-target", "Atlantis", ct), timeout.Token);
        await otherTransaction.CommitAsync();

        addWaited.ShouldBeTrue();
        (await add.WaitAsync(TimeSpan.FromSeconds(30))).FirstError.Code.ShouldBe("Taxonomy.AliasAlreadyExists");
        // The unique violation ends the work: the transaction is only rolled back, with no statement after the insert.
        log.Commands[^1].ShouldContain("INSERT INTO romd.\"RegionAliases\"");
        await using var check = database.CreateContext();
        (await check.RegionAliases.SingleAsync()).RegionId.ShouldBe(source.Id);
        (await check.AdminAuditEvents.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Merge_IntoATargetMergedAwayWhileItWaits_IsUnknownTargetAndKeepsTheSource()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedSourceAndTargetAsync(database);
        await using (var db = database.CreateContext())
        {
            db.Regions.Add(new RegionEntity { Name = "Other", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Other") });
            await db.SaveChangesAsync();
        }

        // The target's own merge queues first, so it removes the target before the waiting merge can lock it.
        await using var hold = await RegionRowHold.AcquireAsync(database, target.Id);
        await using var targetMergeDb = database.CreateContext();
        await using var sourceMergeDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (targetMerge, targetMergeWaited) = await database.StartWaitingForALockAsync(targetMergeDb,
            ct => Service(targetMergeDb).MergeAsync("dat-target", "dat-other", ct), timeout.Token);
        var (sourceMerge, sourceMergeWaited) = await database.StartWaitingForALockAsync(sourceMergeDb,
            ct => Service(sourceMergeDb).MergeAsync("dat-source", "dat-target", ct), timeout.Token);
        await hold.ReleaseAsync();

        targetMergeWaited.ShouldBeTrue();
        sourceMergeWaited.ShouldBeTrue();
        (await targetMerge.WaitAsync(TimeSpan.FromSeconds(30))).IsError.ShouldBeFalse();
        (await sourceMerge.WaitAsync(TimeSpan.FromSeconds(30))).FirstError.Code.ShouldBe("Taxonomy.UnknownTarget");
        await using var check = database.CreateContext();
        (await check.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync())
            .ShouldBe(["dat-source", "dat-other"]);
    }

    private static async Task<int> SeedAliasAsync(PostgreSqlTestDatabase database, int regionId, string alias)
    {
        await using var db = database.CreateContext();
        var row = new RegionAliasEntity { RegionId = regionId, NormalizedAlias = alias };
        db.RegionAliases.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    // One committed DAT game, with the minimum source chain a DAT game needs.
    private static async Task<int> SeedDatGameAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var platform = new PlatformEntity
        {
            Name = "Lock test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Lock test",
            BaseCompactLabel = "Lock test",
            CanonicalKey = "lock-test",
            ShortName = "lock-test"
        };
        var catalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" };
        var file = new FileEntityPersistence { Sha256 = Sha256.FromBytes(new byte[32]), Size = 1, SizeOnDisk = 1 };
        db.AddRange(platform, catalogSource, file);
        await db.SaveChangesAsync();
        var datSource = new DatSourceEntity { CatalogSourceId = catalogSource.Id };
        var entry = new SourceEntryEntity { CatalogSourceId = catalogSource.Id, EntryKey = "Game", Name = "Game", PlatformId = platform.Id };
        db.AddRange(datSource, entry);
        await db.SaveChangesAsync();
        var datFile = new DatFileEntity
        {
            DatSourceId = datSource.Id,
            Name = "Lock test",
            Description = "Lock test",
            Type = DatType.NoIntro.ToString(),
            PlatformId = platform.Id,
            OriginalFilename = "lock-test.dat",
            FileId = file.Id
        };
        db.DatFiles.Add(datFile);
        await db.SaveChangesAsync();
        var game = new DatGameEntity { DatFileId = datFile.Id, SourceEntryId = entry.Id, Name = "Game" };
        db.DatGames.Add(game);
        await db.SaveChangesAsync();
        return game.Id;
    }

    // One canonical release, with the platform and title it belongs to.
    private static async Task<int> SeedCatalogReleaseAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var platform = new PlatformEntity
        {
            Name = "Rebuild test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Rebuild test",
            BaseCompactLabel = "Rebuild test",
            CanonicalKey = "rebuild-test",
            ShortName = "rebuild-test"
        };
        db.Platforms.Add(platform);
        await db.SaveChangesAsync();
        var title = new TitleEntity { PlatformId = platform.Id, Name = "Test", NormalizedName = "test", EnrichmentStatus = "Pending" };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        var release = new CatalogReleaseEntity { PlatformId = platform.Id, CatalogTitleId = title.Id, Name = "Release", Fingerprint = "test" };
        db.CatalogReleases.Add(release);
        await db.SaveChangesAsync();
        return release.Id;
    }

    private sealed class CatalogRebuildMergeGate : DbCommandInterceptor
    {
        private int _sourceLockAttempts;
        public int Deadlocks { get; private set; }
        public TaskCompletionSource SourceLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueFirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryStarting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE") && Interlocked.Increment(ref _sourceLockAttempts) == 2)
            {
                RetryStarting.SetResult();
                await ContinueRetry.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE") && _sourceLockAttempts == 1)
            {
                SourceLocked.SetResult();
                await ContinueFirstAttempt.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
                Deadlocks++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     A catalog rebuild of a release's platform, uncommitted: it deletes the release's region links, then inserts
    ///     them again, and its foreign-key check holds each re-linked region <c>FOR KEY SHARE</c> until it commits.
    /// </summary>
    private sealed class CatalogRebuildHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public int ProcessId => connection.ProcessID;

        public static async Task<CatalogRebuildHold> DeleteLinksAsync(PostgreSqlTestDatabase database, int releaseId)
        {
            var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            var hold = new CatalogRebuildHold(connection, transaction);
            // Only the merge's deadlock check may break the cycle, so the merge is always its victim.
            await hold.ExecuteAsync("SET LOCAL deadlock_timeout = '1min'");
            await hold.ExecuteAsync($"""DELETE FROM romd."CatalogReleaseRegions" WHERE "CatalogReleaseId" = {releaseId}""");
            return hold;
        }

        public Task InsertLinkAsync(int releaseId, int regionId, CancellationToken ct) => ExecuteAsync(
            $"""INSERT INTO romd."CatalogReleaseRegions" ("CatalogReleaseId", "RegionId") VALUES ({releaseId}, {regionId})""", ct);

        public Task CommitAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }

        private async Task ExecuteAsync(string sql, CancellationToken ct = default)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    ///     A running ingest's link from a DAT game to a region or language, uncommitted: its foreign-key check holds the
    ///     row <c>FOR KEY SHARE</c> until the ingest commits.
    /// </summary>
    private sealed class IngestLinkHold(RomdDbContext context, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public static Task<IngestLinkHold> AcquireAsync(PostgreSqlTestDatabase database, int datGameId, int regionId) =>
            AcquireAsync(database, new DatGameRegionEntity { DatGameId = datGameId, RegionId = regionId });

        public static Task<IngestLinkHold> AcquireLanguageAsync(PostgreSqlTestDatabase database, int datGameId, int languageId) =>
            AcquireAsync(database, new DatGameLanguageEntity { DatGameId = datGameId, GameLanguageId = languageId });

        private static async Task<IngestLinkHold> AcquireAsync(PostgreSqlTestDatabase database, object link)
        {
            var context = database.CreateContext();
            var transaction = await context.Database.BeginTransactionAsync();
            context.Add(link);
            await context.SaveChangesAsync();
            return new IngestLinkHold(context, transaction);
        }

        public Task CommitAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await context.DisposeAsync();
        }
    }

    // The pair the concurrency tests merge; the source is added first, so a merge locks it first.
    private static async Task<(RegionEntity Source, RegionEntity Target)> SeedSourceAndTargetAsync(PostgreSqlTestDatabase database)
    {
        var source = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        var target = new RegionEntity { Name = "Target", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        await using var db = database.CreateContext();
        db.Regions.AddRange(source, target);
        await db.SaveChangesAsync();
        return (source, target);
    }

    private static async Task<(GameLanguageEntity Source, GameLanguageEntity Target)> SeedLanguageSourceAndTargetAsync(
        PostgreSqlTestDatabase database)
    {
        var source = new GameLanguageEntity { Name = "Source", Code = "SRC", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        var target = new GameLanguageEntity { Name = "Target", Code = "DST", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        await using var db = database.CreateContext();
        db.GameLanguages.AddRange(source, target);
        await db.SaveChangesAsync();
        return (source, target);
    }

    /// <summary>Holds a region row as a merge does; later lockers resolve it first, then queue for it in arrival order.</summary>
    private sealed class RegionRowHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public static async Task<RegionRowHold> AcquireAsync(PostgreSqlTestDatabase database, int regionId)
        {
            var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var lockRow = new NpgsqlCommand(
                $"""SELECT 1 FROM romd."Regions" WHERE "Id" = {regionId} FOR UPDATE""", connection, transaction);
            await lockRow.ExecuteNonQueryAsync();
            return new RegionRowHold(connection, transaction);
        }

        public Task ReleaseAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static TaxonomyService<Region> Service(RomdDbContext db) =>
        new(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);

    private static TaxonomyService<GameLanguage> LanguageService(RomdDbContext db) =>
        new(new LanguageRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<GameLanguage>>.Instance);

    private static TaxonomyResolver<Region> Resolver(RomdDbContext db) =>
        new(new RegionRepository(db), new EfUnitOfWork(db), token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true));

    [Fact]
    public async Task AddAlias_TakenAlias_IsAConflictAndReturnsTheCreatedAliasOtherwise()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var region = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);

        var created = (await service.AddAliasAsync("dat-source", "  Local Alias ", default)).Value;

        created.Alias.ShouldBe("local alias");
        created.Ownership.ShouldBe("Installation");
        (await db.RegionAliases.SingleAsync(alias => alias.Id == created.Id)).RegionId.ShouldBe(region.Id);
        (await service.AddAliasAsync("dat-source", "LOCAL ALIAS", default)).FirstError.Code.ShouldBe("Taxonomy.AliasAlreadyExists");
        var repository = new RegionRepository(db);
        var unitOfWork = new EfUnitOfWork(db);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        var duplicate = await repository.AddAliasStagedAsync(region.Id, "local alias");
        var failure = await Should.ThrowAsync<DbUpdateException>(() => unitOfWork.FlushAsync());
        repository.IsAliasConflict(failure).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => duplicate.ReadAfterFlush());
    }
}
