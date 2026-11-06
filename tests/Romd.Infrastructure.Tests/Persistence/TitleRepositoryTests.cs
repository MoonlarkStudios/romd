using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class TitleRepositoryTests : IDisposable
{
    private const int PlatformId = 1;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TitleRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedPlatform(db);
    }

    [Fact]
    public async Task HasTrackedAsync_OnlyMatchesCurrentTrackingIntent()
    {
        using var db = CreateDb();
        ITrackedTitleReader repository = new TrackedTitleRepository(db, TimeProvider.System);
        (await repository.HasTrackedAsync()).ShouldBeFalse();
        await AddTitleAsync(db, id: 1, isTracked: true);
        await AddTitleAsync(db, id: 2, isTracked: false);
        (await repository.HasTrackedAsync()).ShouldBeTrue();
        (await repository.HasTrackedAsync([1, 2])).ShouldBeTrue();
        (await repository.HasTrackedAsync([2])).ShouldBeFalse();
        (await repository.HasTrackedAsync([])).ShouldBeFalse();
        await db.TrackedTitles.ExecuteDeleteAsync();
        (await repository.HasTrackedAsync([1])).ShouldBeFalse();
    }

    [Fact]
    public async Task AddUserMedia_RetryDeduplicatesByContentAndTypeWithoutRotating()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = NewSha256(1), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var staged = new List<(bool Added, bool Rotated)>();
        foreach (var type in new[] { MediaType.Screenshot, MediaType.Screenshot, MediaType.Cover })
        {
            using var attempt = CreateDb();
            await using var transaction = await attempt.Database.BeginTransactionAsync();
            var before = await attempt.Titles.Where(title => title.Id == 1).Select(title => title.Revision).SingleAsync();
            var result = await new TitleRepository(attempt)
                .AddUserMediaStagedAsync(TitleMedia.CreateNew(1, type, 1, "user", "image/png", createdAt: DateTimeOffset.UnixEpoch));
            await attempt.SaveChangesAsync();
            await transaction.CommitAsync();
            var after = await db.Titles.Where(title => title.Id == 1).Select(title => title.Revision).SingleAsync();
            result.TitleRevision.ShouldBe(after);
            staged.Add((result.Added, after != before));
        }
        // The retry is a no-op: no row and no new revision.
        staged.ShouldBe([(true, true), (false, false), (true, true)]);
        var media = await db.TitleMedia.Where(item => item.TitleId == 1).ToListAsync();
        media.Count.ShouldBe(2);
        media.Count(item => item.Type == "Screenshot").ShouldBe(1);
        media.ShouldAllBe(item => item.IsPrimary);
    }

    [Fact]
    public async Task AddUserMedia_ConcurrentIdenticalUploads_SecondWaitsForTheRowLockAndAddsNothing()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = NewSha256(1), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = CreateDb();
        await using var firstTransaction = await first.Database.BeginTransactionAsync(timeout.Token);
        var added = await new TitleRepository(first)
            .AddUserMediaStagedAsync(TitleMedia.CreateNew(1, MediaType.Cover, 1, "user", "image/png", createdAt: DateTimeOffset.UnixEpoch), timeout.Token);
        await first.SaveChangesAsync(timeout.Token);

        await using var second = CreateDb();
        await second.Database.OpenConnectionAsync(timeout.Token);
        var pid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        await using var secondTransaction = await second.Database.BeginTransactionAsync(timeout.Token);
        var repeat = new TitleRepository(second)
            .AddUserMediaStagedAsync(TitleMedia.CreateNew(1, MediaType.Cover, 1, "user", "image/png", createdAt: DateTimeOffset.UnixEpoch), timeout.Token);
        await using var observer = CreateDb();
        var waited = false;
        while (!repeat.IsCompleted && !waited)
        {
            // Observe PostgreSQL's lock queue rather than assuming the repeat reached the lock by now.
            waited = await observer.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {pid} AND NOT granted) AS \"Value\"")
                .SingleAsync(timeout.Token);
            if (!waited)
                await Task.Delay(10, timeout.Token);
        }
        await firstTransaction.CommitAsync(timeout.Token);
        var repeated = await repeat.WaitAsync(timeout.Token);
        await second.SaveChangesAsync(timeout.Token);
        await secondTransaction.CommitAsync(timeout.Token);

        waited.ShouldBeTrue();
        (added.Added, repeated.Added).ShouldBe((true, false));
        repeated.TitleRevision.ShouldBe(added.TitleRevision);
        (await observer.TitleMedia.CountAsync(item => item.TitleId == 1, timeout.Token)).ShouldBe(1);
    }

    [Fact]
    public async Task AppendGalleryMedia_MultipleScreenshots_RetainsAttributionDeduplicatesAndSurvivesMetadataUpdate()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        for (var id = 1; id <= 2; id++)
            db.Files.Add(new FileEntityPersistence { Id = id, Sha256 = NewSha256((byte)id), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var repository = new TitleRepository(db);
        var first = TitleMedia.CreateNew(1, MediaType.Screenshot, 1, "gallery:igdb", "image/png", DateTimeOffset.UnixEpoch,
            "https://images.igdb.com/first.png", attribution: "Artist", sourcePageUrl: "https://www.igdb.com/games/test");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var added = await repository.AppendGalleryMediaStagedAsync(first);
            await db.SaveChangesAsync();
            // A repeat writes nothing and keeps the revision the first import rotated to.
            var repeated = await repository.AppendGalleryMediaStagedAsync(first);
            var another = await repository.AppendGalleryMediaStagedAsync(TitleMedia.CreateNew(1, MediaType.Screenshot, 2, "gallery:igdb", "image/png", createdAt: DateTimeOffset.UnixEpoch));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            (added.Added, repeated.Added, another.Added).ShouldBe((true, false, true));
            repeated.TitleRevision.ShouldBe(added.TitleRevision);
            another.TitleRevision.ShouldNotBe(added.TitleRevision);
        }
        var title = (await repository.GetAggregateAsync(1))!;
        title.Media.Count.ShouldBe(2);
        title.Media.ShouldAllBe(media => !media.IsPrimary);
        title.Media.Single(media => media.FileId == 1).Attribution.ShouldBe("Artist");
        title.StoreUserLayer(new TitleMetadataPayload { Description = "Updated" }, updatedAt: DateTimeOffset.UnixEpoch);
        title.Rematerialize(["igdb"]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await repository.UpdateUserMetadataStagedAsync(title);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        using var verify = CreateDb();
        var retained = await verify.TitleMedia.Where(media => media.TitleId == 1).ToListAsync();
        retained.Count.ShouldBe(2);
        retained.Single(media => media.FileId == 1).SourcePageUrl.ShouldBe("https://www.igdb.com/games/test");
        (await verify.ArtworkSelections.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TrackByTitleIdsAsync_PartlyTrackedInput_TracksAllIdempotently()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: true);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        await repository.TrackByTitleIdsAsync([1, 2]);

        (await GetTrackedAsync(1)).ShouldBeTrue();
        (await GetTrackedAsync(2)).ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_DetachedDomainTitle_PreservesProjectionOwnedHasLocalPayload()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await db.Titles
            .Where(title => title.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.HasLocalPayload, true)
                .SetProperty(title => title.CatalogState, TitleCatalogState.UserOnly)
                .SetProperty(title => title.FieldProvenanceJson, "{\"Description\":\"igdb\"}")
                .SetProperty(title => title.FieldSourceOverridesJson, "{\"Description\":\"igdb\"}")
                .SetProperty(title => title.ScreenshotPrefsJson, "{\"preserve\":true}"));
        var repository = new TitleRepository(db);
        var detached = await ScalarSnapshotAsync(db, 1);
        detached.QueueForEnrichment();

        await StageAndCommitAsync(db, () => repository.ApplyEnrichmentStagedAsync(detached));

        var persisted = await db.Titles.AsNoTracking().SingleAsync(title => title.Id == 1);
        persisted.HasLocalPayload.ShouldBeTrue();
        persisted.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        persisted.ScreenshotPrefsJson.ShouldBe("{\"preserve\":true}");
        persisted.FieldProvenanceJson.ShouldBe("{\"Description\":\"igdb\"}");
        persisted.FieldSourceOverridesJson.ShouldBe("{\"Description\":\"igdb\"}");
    }

    [Theory]
    [InlineData("enrichment")]
    [InlineData("remove-media")]
    [InlineData("materialized")]
    [InlineData("user")]
    public async Task StagedWrites_ConcurrentEnrichment_RejectStaleSnapshotAndRollBackChildren(string route)
    {
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 1, isTracked: false);
            seed.Files.Add(new FileEntityPersistence
            {
                Id = 1,
                Sha256 = NewSha256(1),
                Size = 10,
                SizeOnDisk = 10,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
            seed.TitleMedia.Add(new TitleMediaEntity
            {
                Id = 1,
                TitleId = 1,
                FileId = 1,
                Type = "Cover",
                SourceId = "igdb",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        using var staleDb = CreateDb();
        using var winnerDb = CreateDb();
        var staleRepository = new TitleRepository(staleDb);
        var winnerRepository = new TitleRepository(winnerDb);
        var stale = (await staleRepository.GetAggregateAsync(1))!;
        var winner = (await winnerRepository.GetAggregateAsync(1))!;
        var initialRevision = stale.Revision;
        initialRevision.ShouldNotBe(Guid.Empty);

        winner.StoreProviderLayer("igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "Concurrent provider description" }, updatedAt: DateTimeOffset.UnixEpoch);
        winner.Rematerialize(["igdb"]);
        await StageAndCommitAsync(winnerDb, () => winnerRepository.ApplyEnrichmentStagedAsync(winner));

        stale.RemoveMedia(1).ShouldBeTrue();
        stale.StoreUserLayer(new TitleMetadataPayload { Description = "Stale user description" }, updatedAt: DateTimeOffset.UnixEpoch);
        stale.Rematerialize(["igdb"]);
        await using (var transaction = await staleDb.Database.BeginTransactionAsync())
        {
            await Should.ThrowAsync<PersistenceConflictException>(async () =>
            {
                if (route == "enrichment")
                    await staleRepository.ApplyEnrichmentStagedAsync(stale);
                else if (route == "remove-media")
                    await staleRepository.RemoveMediaStagedAsync(stale, 1);
                else if (route == "materialized")
                    await staleRepository.UpdateMaterializedMetadataStagedAsync(stale);
                else
                    await staleRepository.UpdateUserMetadataStagedAsync(stale);
                await staleDb.SaveChangesAsync();
            });
            await transaction.RollbackAsync();
        }

        using var verify = CreateDb();
        var stored = (await new TitleRepository(verify).GetAggregateAsync(1))!;
        stored.Media.ShouldContain(media => media.Id == 1);
        stored.Revision.ShouldNotBe(initialRevision);
        stored.Description.ShouldBe("Concurrent provider description");
        stored.GetMetadataLayer("user").ShouldBeNull();
        stored.GetMetadataLayer("igdb")!.GetPayload()!.Description.ShouldBe("Concurrent provider description");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedWrites_ConcurrentFirstUserLayers_ReturnConflictWithoutReplacingWinner(bool userRoute)
    {
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 1, isTracked: false);
        }
        using var firstDb = CreateDb();
        using var staleDb = CreateDb();
        var firstRepository = new TitleRepository(firstDb);
        var staleRepository = new TitleRepository(staleDb);
        var first = (await firstRepository.GetAggregateAsync(1))!;
        var stale = (await staleRepository.GetAggregateAsync(1))!;
        first.StoreUserLayer(new TitleMetadataPayload { Description = "Winner" }, updatedAt: DateTimeOffset.UnixEpoch);
        first.Rematerialize([]);
        await StageAndCommitAsync(firstDb, () => firstRepository.UpdateUserMetadataStagedAsync(first));
        stale.StoreUserLayer(new TitleMetadataPayload { Description = "Stale" }, updatedAt: DateTimeOffset.UnixEpoch);
        stale.Rematerialize([]);

        await using (var transaction = await staleDb.Database.BeginTransactionAsync())
        {
            await Should.ThrowAsync<PersistenceConflictException>(async () =>
            {
                if (userRoute)
                    await staleRepository.UpdateUserMetadataStagedAsync(stale);
                else
                    await staleRepository.ApplyEnrichmentStagedAsync(stale);
                await staleDb.SaveChangesAsync();
            });
            await transaction.RollbackAsync();
        }

        using var verify = CreateDb();
        var stored = (await new TitleRepository(verify).GetAggregateAsync(1))!;
        stored.Description.ShouldBe("Winner");
        stored.MetadataLayers.Count.ShouldBe(1);
        stored.GetMetadataLayer("user")!.GetPayload()!.Description.ShouldBe("Winner");
    }

    [Fact]
    public async Task RemoveMediaStagedAsync_PrimaryRemoved_DeletesOnlyThatRowAndPersistsRecalculatedPrimaries()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(1))!;
        var revision = title.Revision;
        // DeleteTitleMedia's domain step: remove the primary cover, then recalculate every type.
        title.RemoveMedia(1).ShouldBeTrue();
        title.RecalculatePrimaryMedia(["igdb"]);

        await StageAndCommitAsync(db, () => repository.RemoveMediaStagedAsync(title, 1));

        using var verify = CreateDb();
        var media = await verify.TitleMedia.Where(row => row.TitleId == 1).OrderBy(row => row.Id).ToListAsync();
        media.Select(row => (row.Id, row.Type, row.IsPrimary)).ShouldBe([(2, "Cover", true), (3, "Screenshot", true)]);
        var stored = await verify.Titles.SingleAsync(row => row.Id == 1);
        stored.Revision.ShouldNotBe(revision);
        stored.EnrichmentStatus.ShouldBe(nameof(EnrichmentStatus.LowConfidence));
        stored.HasLocalPayload.ShouldBeTrue();
        stored.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        stored.ScreenshotPrefsJson.ShouldBe("{\"preserve\":true}");
        stored.FieldSourceOverridesJson.ShouldBe("{\"Genre\":\"user\"}");
        stored.Description.ShouldBe("Curated description");
        var links = await verify.TitleExternalIds.Where(link => link.TitleId == 1).OrderBy(link => link.Provider).ToListAsync();
        links.Select(link => (link.Provider, link.ExternalId, link.MatchConfidence, link.IsConfirmed))
            .ShouldBe([("igdb", "123", 0.5f, false), ("steamgriddb", "77", 0.9f, false)]);
        var layers = await verify.TitleMetadataLayers.Where(layer => layer.TitleId == 1).OrderBy(layer => layer.SourceId).ToListAsync();
        layers.Select(layer => layer.MetadataJson)
            .ShouldBe(["{\"Description\":\"provider\"}", "{\"Description\":\"Curated description\"}"]);
        (await verify.TitleContentRatings.SingleAsync(rating => rating.TitleId == 1)).Code.ShouldBe("E");
        // The file row is untouched; DeleteTitleMedia releases it only after commit, when unreferenced.
        (await verify.Files.CountAsync(file => file.Id == 1)).ShouldBe(1);
    }

    [Fact]
    public async Task RemoveMediaStagedAsync_SnapshotStillContainsMedia_ThrowsWithoutWriting()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(1))!;

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await Should.ThrowAsync<ArgumentException>(() => repository.RemoveMediaStagedAsync(title, 1));
            await transaction.RollbackAsync();
        }

        using var verify = CreateDb();
        (await verify.TitleMedia.CountAsync(row => row.TitleId == 1)).ShouldBe(3);
        (await verify.Titles.SingleAsync(row => row.Id == 1)).Revision.ShouldBe(title.Revision);
    }

    [Fact]
    public async Task UpdatePrimaryMediaStagedAsync_Commit_ChangesOnlyThatTypesPrimaryFlags()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(1))!;
        var revision = title.Revision;
        title.SetPrimaryMedia(2);

        await StageAndCommitAsync(db, () => repository.UpdatePrimaryMediaStagedAsync(title, MediaType.Cover));

        using var verify = CreateDb();
        var media = await verify.TitleMedia.Where(row => row.TitleId == 1).OrderBy(row => row.Id).ToListAsync();
        media.Select(row => (row.Id, row.IsPrimary)).ShouldBe([(1, false), (2, true), (3, true)]);
        await AssertCuratedStatePreservedAsync(verify, revision);
    }

    [Fact]
    public async Task GetAggregateAsync_ExistingTitle_LoadsEveryChildCollection()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();

        var title = (await new TitleRepository(db).GetAggregateAsync(1))!;

        title.ExternalIds.Select(link => link.Provider).Order().ShouldBe(["igdb", "steamgriddb"]);
        title.Media.Select(media => media.Id).Order().ShouldBe([1, 2, 3]);
        title.MetadataLayers.Select(layer => layer.SourceId).Order().ShouldBe(["igdb", "user"]);
        title.ContentRatingsMaterialized.ShouldBeTrue();
        title.ContentRatings.ShouldHaveSingleItem();
        db.ChangeTracker.Entries().ShouldBeEmpty("the aggregate is a detached snapshot");
        (await new TitleRepository(db).GetAggregateAsync(99)).ShouldBeNull();
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_SnapshotWithoutChildren_NeverDeletesThem()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = await ScalarSnapshotAsync(db, 1);
        var revision = title.Revision;
        title.MarkEnrichmentNotFound(occurredAt: DateTimeOffset.UnixEpoch);

        await StageAndCommitAsync(db, () => repository.ApplyEnrichmentStagedAsync(title));

        using var verify = CreateDb();
        (await verify.Titles.SingleAsync(row => row.Id == 1)).EnrichmentStatus.ShouldBe(nameof(EnrichmentStatus.NotFound));
        (await verify.TitleMedia.CountAsync(row => row.TitleId == 1)).ShouldBe(3);
        await AssertCuratedStatePreservedAsync(verify, revision);
    }

    [Fact]
    public async Task ApplyMergeStagedAsync_SourceChangedAfterLoad_RejectsMergeWithoutTargetWrites()
    {
        await SeedCuratedTitleAsync();
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 2, isTracked: false);
        }

        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var source = (await repository.GetAggregateAsync(2))!;
        var target = (await repository.GetAggregateAsync(1))!;
        using (var concurrent = CreateDb())
        {
            await concurrent.Titles.Where(row => row.Id == 2).ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.Revision, Guid.NewGuid()));
        }

        target.Absorb(source, ["igdb"], [], new(DateTimeOffset.UnixEpoch,
            source.MetadataLayers.Select(layer => new KeyValuePair<string, DateTimeOffset>(layer.SourceId, DateTimeOffset.UnixEpoch))));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await Should.ThrowAsync<PersistenceConflictException>(() =>
                repository.ApplyMergeStagedAsync(target, source));
            await transaction.RollbackAsync();
        }

        using var verify = CreateDb();
        (await verify.Titles.SingleAsync(row => row.Id == 1)).Revision.ShouldBe(target.Revision);
    }

    [Fact]
    public async Task TitleStagedWrites_WithoutCallerTransaction_ThrowBeforeWriting()
    {
        await SeedCuratedTitleAsync();
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(1))!;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            repository.UpdatePrimaryMediaStagedAsync(title, MediaType.Cover));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.ApplyEnrichmentStagedAsync(title));
        await Should.ThrowAsync<InvalidOperationException>(() => repository.ApplyMergeStagedAsync(title, title));
        title.RemoveMedia(2).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => repository.RemoveMediaStagedAsync(title, 2));

        using var verify = CreateDb();
        (await verify.Titles.SingleAsync(row => row.Id == 1)).Revision.ShouldBe(title.Revision);
    }

    [Fact]
    public async Task UpdateUserMetadataStagedAsync_Commit_ChangesUserLayerAndPreservesProviderAndPersistenceState()
    {
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 1, isTracked: false);
            await seed.Titles.Where(title => title.Id == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(title => title.CatalogState, TitleCatalogState.UserOnly)
                .SetProperty(title => title.ScreenshotPrefsJson, "{\"preserve\":true}"));
            seed.TitleMetadataLayers.Add(new TitleMetadataLayerEntity
            {
                TitleId = 1,
                SourceId = "igdb",
                SourceType = (int)MetadataSourceType.Provider,
                MetadataJson = "{\"Description\":\"provider\"}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(1))!;
        var revision = title.Revision;
        title.StoreUserLayer(new TitleMetadataPayload { Description = "User description" }, updatedAt: DateTimeOffset.UnixEpoch);
        title.Rematerialize(["igdb"]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await repository.UpdateUserMetadataStagedAsync(title);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using var verify = CreateDb();
        var stored = await verify.Titles.SingleAsync(candidate => candidate.Id == 1);
        stored.Description.ShouldBe("User description");
        stored.Revision.ShouldNotBe(revision);
        stored.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        stored.ScreenshotPrefsJson.ShouldBe("{\"preserve\":true}");
        var layers = await verify.TitleMetadataLayers.Where(layer => layer.TitleId == 1).ToListAsync();
        layers.Count.ShouldBe(2);
        layers.Single(layer => layer.SourceId == "igdb").MetadataJson.ShouldBe("{\"Description\":\"provider\"}");
        layers.Single(layer => layer.SourceId == "user").ToDomain().GetPayload()!.Description.ShouldBe("User description");
    }

    [Fact]
    public async Task UntrackAsync_ExistingTitle_RemovesIntentAndReturnsTrue()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        bool result = await repository.UntrackAsync(1);

        result.ShouldBeTrue();
        (await GetTrackedAsync(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrackAsync_MissingTitle_ReturnsNotFound()
    {
        using var db = CreateDb();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        var result = await repository.TrackAsync(999);

        result.ShouldBe(TrackTitleResult.TitleNotFound);
    }

    [Fact]
    public async Task TrackByTitleIdsAsync_TracksGivenIds_AndReturnsCount()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        await AddTitleAsync(db, id: 3, isTracked: false);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        int updated = await repository.TrackByTitleIdsAsync([1, 2]);

        updated.ShouldBe(2);
        (await GetTrackedAsync(1)).ShouldBeTrue();
        (await GetTrackedAsync(2)).ShouldBeTrue();
        (await GetTrackedAsync(3)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrackByTitleIdsAsync_RepeatedRequest_IsIdempotent()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        int first = await repository.TrackByTitleIdsAsync([1, 1]);
        int second = await repository.TrackByTitleIdsAsync([1]);

        first.ShouldBe(1);
        second.ShouldBe(0);
        (await db.TrackedTitles.CountAsync(t => t.TitleId == 1)).ShouldBe(1);
    }

    [Fact]
    public async Task UntrackByTitleIdsAsync_CanUntrack()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);
        await AddTitleAsync(db, id: 2, isTracked: true);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        await repository.UntrackByTitleIdsAsync([1, 2]);

        (await GetTrackedAsync(1)).ShouldBeFalse();
        (await GetTrackedAsync(2)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrackByTitleIdsAsync_EmptyInput_ReturnsZero()
    {
        using var db = CreateDb();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        int updated = await repository.TrackByTitleIdsAsync([]);

        updated.ShouldBe(0);
    }

    [Fact]
    public async Task TrackByPlatformAsync_TracksOnlyPlatformTitles()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Nintendo 64",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Nintendo 64",
            BaseCompactLabel = "Nintendo 64",
            CanonicalKey = "n64",
            ShortName = "n64",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 2,
            PlatformId = 2,
            Name = "Other",
            NormalizedName = "other",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        int affected = await repository.TrackByPlatformAsync(PlatformId);

        affected.ShouldBe(1);
        (await GetTrackedAsync(1)).ShouldBeTrue();
        (await GetTrackedAsync(2)).ShouldBeFalse();

        (await repository.UntrackByPlatformAsync(PlatformId)).ShouldBe(1);
        (await GetTrackedAsync(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrackByDatSourceAsync_OnlyUsesActiveVersionMappings()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        await AddTitleAsync(db, id: 3, isTracked: false);
        SeedDatGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 1, NewSha1(0x31), "Source Game");
        await db.SaveChangesAsync();
        int sourceId = await db.DatFiles.Where(dat => dat.Id == 1).Select(dat => dat.DatSourceId).SingleAsync();
        SeedDatGame(
            db, datFileId: 2, gameId: 2, romId: 2, titleId: 2, NewSha1(0x32), "Pending Game",
            sourceId, DatFileLifecycle.PendingActivation);
        SeedDatGame(
            db, datFileId: 3, gameId: 3, romId: 3, titleId: 3, NewSha1(0x33), "Superseded Game",
            sourceId, DatFileLifecycle.Superseded);
        await db.SaveChangesAsync();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        int affected = await repository.TrackByDatSourceAsync(sourceId);

        affected.ShouldBe(1);
        (await GetTrackedAsync(1)).ShouldBeTrue();
        (await GetTrackedAsync(2)).ShouldBeFalse();
        (await GetTrackedAsync(3)).ShouldBeFalse();

        await repository.TrackByTitleIdsAsync([2, 3]);
        (await repository.UntrackByDatSourceAsync(sourceId)).ShouldBe(1);
        (await GetTrackedAsync(1)).ShouldBeFalse();
        (await GetTrackedAsync(2)).ShouldBeTrue();
        (await GetTrackedAsync(3)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetDetailAsync_PreferenceUsesConfiguredRegionsAndPin()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        var usa = CatalogRelease(id: 11, titleId: 1);
        usa.Region = "USA";
        var japan = CatalogRelease(id: 12, titleId: 1);
        japan.Region = "Japan";
        db.CatalogReleases.AddRange(usa, japan);
        await db.SaveChangesAsync();
        var options = Microsoft.Extensions.Options.Options.Create(new Romd.Admin.Application.Configuration.EnrichmentOptions { RegionPriority = ["JAPAN", "USA"] });
        var repository = new TitleDetailReader(db, options);
        var automatic = await repository.GetDetailAsync(1);
        automatic.ShouldNotBeNull();
        automatic.PreferredCatalogReleaseId.ShouldBe(12);
        automatic.ReleaseRegionPriority.ShouldBe(["JAPAN", "USA"]);
        await new TrackedTitleRepository(db, TimeProvider.System).TrackAsync(1, 11);
        var pinned = await repository.GetDetailAsync(1);
        pinned!.PreferredCatalogReleaseId.ShouldBe(11);
        pinned.PinnedCatalogReleaseId.ShouldBe(11);
        await new TrackedTitleRepository(db, TimeProvider.System).TrackAsync(1, null);
        (await repository.GetDetailAsync(1))!.PreferredCatalogReleaseId.ShouldBe(12);
    }

    [Fact]
    public async Task TrackAsync_PinMustBelongToTitle_AndReleaseDeletionClearsPin()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        db.CatalogReleases.AddRange(
            CatalogRelease(id: 11, titleId: 1),
            CatalogRelease(id: 22, titleId: 2));
        await db.SaveChangesAsync();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        (await repository.TrackAsync(1, 22)).ShouldBe(TrackTitleResult.PinnedCatalogReleaseTitleMismatch);
        (await repository.TrackAsync(1, 11)).ShouldBe(TrackTitleResult.Updated);
        (await repository.TrackAsync(1, 11)).ShouldBe(TrackTitleResult.Updated);
        (await db.TrackedTitles.CountAsync(tracked => tracked.TitleId == 1)).ShouldBe(1);
        (await repository.GetAsync(1))!.PinnedCatalogReleaseId.ShouldBe(11);

        await db.CatalogReleases.Where(release => release.Id == 11).ExecuteDeleteAsync();

        var tracked = await repository.GetAsync(1);
        tracked.ShouldNotBeNull();
        tracked.PinnedCatalogReleaseId.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAbsorbedSourceAsync_TrackedTitle_RetainsUserOnlyTitle()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);

        await new TitleRepository(db).DeleteAbsorbedSourceAsync(1);

        var title = await db.Titles.SingleAsync(t => t.Id == 1);
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await db.TrackedTitles.AnyAsync(t => t.TitleId == 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task DatabaseDelete_TrackedTitle_IsRestrictedUntilUntracked()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        await Should.ThrowAsync<PostgresException>(() =>
            db.Titles.Where(title => title.Id == 1).ExecuteDeleteAsync());

        (await repository.UntrackAsync(1)).ShouldBeTrue();
        (await db.Titles.Where(title => title.Id == 1).ExecuteDeleteAsync()).ShouldBe(1);
        (await db.Titles.AnyAsync(title => title.Id == 1)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_TrackedOrphan_RetainsForMoveGameFlow()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        deleted.ShouldBeFalse();
        (await db.Titles.SingleAsync(t => t.Id == 1)).CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await db.TrackedTitles.AnyAsync(t => t.TitleId == 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_UntrackedTitleRelinkedAfterOrphanCheck_MutatesNothing()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.CatalogReleases.Add(CatalogRelease(id: 11, titleId: 1));
        // The link stands in for a concurrent derivation that re-linked the title after
        // the caller's orphan check but before this call mutates.
        SeedLinkedEntry(db, entryId: 500, titleId: 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        deleted.ShouldBeFalse();
        (await db.Titles.SingleAsync(t => t.Id == 1)).CatalogState.ShouldBe(TitleCatalogState.Active);
        (await db.CatalogReleases.AnyAsync(r => r.CatalogTitleId == 1)).ShouldBeTrue();
        (await db.TitleSourceLinks.AnyAsync(l => l.TitleId == 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_RelinkedTitleWithoutReleases_IsNotDeleted()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        SeedLinkedEntry(db, entryId: 500, titleId: 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        // Without the mutation-time link re-check, the hard delete removes the re-linked
        // title and silently cascades away the fresh link (TitleSourceLinks.TitleId cascades).
        deleted.ShouldBeFalse();
        (await db.Titles.AnyAsync(t => t.Id == 1)).ShouldBeTrue();
        (await db.TitleSourceLinks.AnyAsync(l => l.TitleId == 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_TrackedTitleRelinkedAfterOrphanCheck_DoesNotDowngradeToUserOnly()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);
        SeedLinkedEntry(db, entryId: 500, titleId: 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        deleted.ShouldBeFalse();
        // A re-linked title is source-backed again: the UserOnly downgrade must not apply.
        (await db.Titles.SingleAsync(t => t.Id == 1)).CatalogState.ShouldBe(TitleCatalogState.Active);
        (await db.TrackedTitles.AnyAsync(t => t.TitleId == 1)).ShouldBeTrue();
        (await db.TitleSourceLinks.AnyAsync(l => l.TitleId == 1)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_WithCallerTransaction_DeletesOrphanTitleAndItsReleases()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.CatalogReleases.AddRange(
            CatalogRelease(id: 11, titleId: 1),
            CatalogRelease(id: 12, titleId: 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        bool deleted;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            deleted = await new TitleOrphanRepository(db).DeleteOrRetainAsync(1);
            await transaction.CommitAsync();
        }

        deleted.ShouldBeTrue();
        (await db.Titles.AnyAsync(t => t.Id == 1)).ShouldBeFalse();
        (await db.CatalogReleases.AnyAsync(r => r.CatalogTitleId == 1)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteOrRetainAsync_NoAmbientTransaction_ThrowsAndMutatesNothing()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.CatalogReleases.AddRange(
            CatalogRelease(id: 11, titleId: 1),
            CatalogRelease(id: 12, titleId: 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The zero-links re-check promise only holds when the statements are atomic, so a
        // caller-owned transaction is an enforced precondition, not a convention.
        db.Database.CurrentTransaction.ShouldBeNull();
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new TitleOrphanRepository(db).DeleteOrRetainAsync(1));

        exception.Message.ShouldContain("caller-owned transaction");
        (await db.Titles.SingleAsync(t => t.Id == 1)).CatalogState.ShouldBe(TitleCatalogState.Active);
        (await db.CatalogReleases.CountAsync(r => r.CatalogTitleId == 1)).ShouldBe(2);
    }

    [Fact]
    public async Task DeleteOrRetainAsync_ReleaseStillAssertedByAnotherSource_RepointsInsteadOfDeleting()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        // Two sources asserted the same fingerprint with conflicting titles: entry A
        // (winner) linked title 1, entry B linked title 2; the projection pointed the
        // shared release at the winner.
        SeedLinkedEntry(db, entryId: 500, titleId: 1);
        SeedLinkedEntry(db, entryId: 501, titleId: 2);
        db.CatalogReleases.Add(CatalogRelease(id: 900, titleId: 1));
        db.CatalogReleaseSources.AddRange(
            ReleaseSource(id: 70, releaseId: 900, entryId: 500, assertedTitleId: 1),
            ReleaseSource(id: 71, releaseId: 900, entryId: 501, assertedTitleId: 2));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The winner loses its backing: unlink entry A so title 1 becomes the orphan.
        await db.TitleSourceLinks.Where(l => l.TitleId == 1).ExecuteDeleteAsync();

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        deleted.ShouldBeTrue();
        (await db.Titles.AnyAsync(t => t.Id == 1)).ShouldBeFalse();
        // Release identity is public and fingerprint-stable: entry B still asserts the
        // release, so its row and ID survive, re-pointed to the surviving linked title.
        var release = await db.CatalogReleases.SingleAsync(r => r.Id == 900);
        release.CatalogTitleId.ShouldBe(2);
        (await db.CatalogReleaseSources.CountAsync(s => s.CatalogReleaseId == 900)).ShouldBe(2);
    }

    [Fact]
    public async Task DeleteOrRetainAsync_RepointedRelease_TakesLowestSurvivingLinkedTitleId()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        await AddTitleAsync(db, id: 3, isTracked: false);
        SeedLinkedEntry(db, entryId: 500, titleId: 1);
        // Provenance-row and entry order both favor title 3; only an id-ordered pick
        // makes the interim owner deterministic before projection recovery re-derives it.
        SeedLinkedEntry(db, entryId: 501, titleId: 3);
        SeedLinkedEntry(db, entryId: 502, titleId: 2);
        db.CatalogReleases.Add(CatalogRelease(id: 900, titleId: 1));
        db.CatalogReleaseSources.AddRange(
            ReleaseSource(id: 70, releaseId: 900, entryId: 500, assertedTitleId: 1),
            ReleaseSource(id: 71, releaseId: 900, entryId: 501, assertedTitleId: 3),
            ReleaseSource(id: 72, releaseId: 900, entryId: 502, assertedTitleId: 2));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.TitleSourceLinks.Where(l => l.TitleId == 1).ExecuteDeleteAsync();

        bool deleted = await DeleteOrRetainInCallerTransactionAsync(db, 1);

        deleted.ShouldBeTrue();
        (await db.CatalogReleases.SingleAsync(r => r.Id == 900)).CatalogTitleId.ShouldBe(2);
    }

    [Fact]
    public async Task ConsolidateAsync_SourceTracked_TargetUntracked_TracksTargetWithoutCrossTitlePin()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        db.CatalogReleases.Add(CatalogRelease(id: 11, titleId: 1));
        await db.SaveChangesAsync();
        var repository = new TrackedTitleRepository(db, TimeProvider.System);
        await repository.TrackAsync(1, 11);

        await repository.ConsolidateAsync(1, 2);

        (await repository.GetAsync(1)).ShouldBeNull();
        var target = await repository.GetAsync(2);
        target.ShouldNotBeNull();
        target.PinnedCatalogReleaseId.ShouldBeNull();
        await db.CatalogReleases.Where(release => release.CatalogTitleId == 1).ExecuteDeleteAsync();
        (await db.Titles.Where(title => title.Id == 1).ExecuteDeleteAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task GetCandidatesAsync_TrackedScope_ExcludesUntracked()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.None);
        await AddTitleAsync(db, id: 2, isTracked: false, status: EnrichmentStatus.None);
        var repository = new TitleReadRepository(db);

        var result = await repository.GetCandidatesAsync(PlatformId, EnrichmentScope.Tracked);

        result.Select(t => t.TitleId).ShouldBe([1]);
    }

    [Fact]
    public async Task GetCandidatesAsync_AllScope_IncludesUntracked()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.None);
        await AddTitleAsync(db, id: 2, isTracked: false, status: EnrichmentStatus.None);
        var repository = new TitleReadRepository(db);

        var result = await repository.GetCandidatesAsync(PlatformId, EnrichmentScope.All);

        result.Select(t => t.TitleId).OrderBy(id => id).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task GetCandidatesAsync_ExcludesCompletedAndNotFound()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 2, isTracked: true, status: EnrichmentStatus.NotFound);
        await AddTitleAsync(db, id: 3, isTracked: true, status: EnrichmentStatus.LowConfidence);
        var repository = new TitleReadRepository(db);

        var result = await repository.GetCandidatesAsync(PlatformId, EnrichmentScope.All);

        result.Select(t => t.TitleId).ShouldBe([3]);
    }

    [Fact]
    public async Task GetSummaryAsync_ExistingTitle_ReturnsScalarsAndProjectionOwnedPayloadFact()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false, status: EnrichmentStatus.LowConfidence, name: "Alpha");
        await db.Titles.Where(title => title.Id == 1).ExecuteUpdateAsync(update => update
            .SetProperty(title => title.HasLocalPayload, true)
            .SetProperty(title => title.Publisher, "Publisher")
            .SetProperty(title => title.Players, 2));
        var reader = new TitleReadRepository(db);

        var summary = await reader.GetSummaryAsync(1);

        summary.ShouldNotBeNull();
        summary.Id.ShouldBe(1);
        summary.PlatformId.ShouldBe(PlatformId);
        summary.Name.ShouldBe("Alpha");
        summary.EnrichmentStatus.ShouldBe(EnrichmentStatus.LowConfidence);
        summary.Publisher.ShouldBe("Publisher");
        summary.Players.ShouldBe(2);
        summary.HasLocalPayload.ShouldBeTrue();
        (await reader.GetSummaryAsync(99)).ShouldBeNull();
    }

    [Fact]
    public async Task ListByPlatformAsync_OrdersByNameAndCarriesPayloadFact()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false, name: "Charlie");
        await AddTitleAsync(db, id: 2, isTracked: false, name: "Alpha");
        await AddTitleAsync(db, id: 3, isTracked: false, name: "Bravo");
        await db.Titles.Where(title => title.Id == 3)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.HasLocalPayload, true));
        var reader = new TitleReadRepository(db);

        var all = await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(PlatformId, null, false).Value);
        var owned = await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(PlatformId, null, true).Value);

        all.Items.Select(title => (title.Id, title.HasLocalPayload)).ShouldBe([(2, false), (3, true), (1, false)]);
        owned.Items.Select(title => title.Id).ShouldBe([3]);
        (await reader.GetIdsByPlatformAsync(PlatformId)).Order().ShouldBe([1, 2, 3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task ListByPlatformAsync_EqualNames_PagesWithoutMissingOrDuplicatingTitles(int limit)
    {
        using var db = CreateDb();
        for (int id = 1; id <= 5; id++)
            await AddTitleAsync(db, id, false, name: id < 4 ? "Alpha" : "Beta");
        var reader = new TitleReadRepository(db);
        string? cursor = null;
        List<int> seen = [];
        do
        {
            var request = PlatformTitlePageRequest.Create(PlatformId, null, false, cursor, limit).Value;
            var page = await reader.ListByPlatformAsync(request);
            page.Items.Count.ShouldBeLessThanOrEqualTo(limit);
            seen.AddRange(page.Items.Select(title => title.Id));
            cursor = page.NextCursor;
            page.HasNextPage.ShouldBe(cursor is not null);
            seen.Count.ShouldBeLessThanOrEqualTo(5);
        } while (cursor is not null);
        seen.ShouldBe([1, 2, 3, 4, 5]);
        var exhausted = await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(
            PlatformId, null, false, PlatformTitlePageRequest.Create(PlatformId, null, false).Value.CursorFor("Z", 99)).Value);
        exhausted.Items.ShouldBeEmpty();
        exhausted.NextCursor.ShouldBeNull();
        exhausted.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public async Task ListByPlatformAsync_MaxLengthSupplementaryNames_TraversesEveryPage()
    {
        using var db = CreateDb();
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 500));
        for (int id = 1; id <= 3; id++)
            await AddTitleAsync(db, id, false, name: name);
        var reader = new TitleReadRepository(db);
        string? cursor = null;
        List<int> seen = [];
        do
        {
            var request = PlatformTitlePageRequest.Create(PlatformId, null, false, cursor, limit: 1).Value;
            var page = await reader.ListByPlatformAsync(request);
            page.Items.Count.ShouldBe(1);
            page.Items[0].Name.ShouldBe(name);
            seen.Add(page.Items[0].Id);
            cursor = page.NextCursor;
            seen.Count.ShouldBeLessThanOrEqualTo(3);
        } while (cursor is not null);
        seen.ShouldBe([1, 2, 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListByPlatformAsync_LibraryAndPayloadScope_AppliesBeforeLimitAndFailsClosed(bool payloadOnly)
    {
        using var db = CreateDb();
        for (int id = 1; id <= 5; id++)
            await AddTitleAsync(db, id, false, name: $"Title {id}");
        await db.Titles.Where(title => title.Id == 3 || title.Id == 4)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.HasLocalPayload, true));
        db.Libraries.Add(new LibraryEntity { Id = 99, Name = "Scoped", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        foreach (int id in new[] { 3, 4, 5 })
            db.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
            {
                LibraryId = 99,
                TitleId = id,
                PlatformId = PlatformId,
                ExposedReleaseCount = 1,
                Availability = "Available"
            });
        await db.SaveChangesAsync();
        var reader = new TitleReadRepository(db);
        var page = await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(PlatformId, 99, payloadOnly, limit: 2).Value);
        page.Items.Select(title => title.Id).ShouldBe([3, 4]);
        page.HasNextPage.ShouldBe(!payloadOnly);
        var denied = await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(PlatformId, 0, payloadOnly).Value);
        denied.Items.ShouldBeEmpty();
        denied.HasNextPage.ShouldBeFalse();
        denied.NextCursor.ShouldBeNull();
        await db.Libraries.Where(library => library.Id == 99)
            .ExecuteUpdateAsync(update => update.SetProperty(library => library.ConfigurationState, "Invalid"));
        (await reader.ListByPlatformAsync(PlatformTitlePageRequest.Create(PlatformId, 99, payloadOnly).Value)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCandidatesAsync_CarriesProjectionOwnedPayloadFact()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, name: "Beta");
        await AddTitleAsync(db, id: 2, isTracked: true, name: "Alpha");
        await db.Titles.Where(title => title.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(title => title.HasLocalPayload, true));

        var candidates = await new TitleReadRepository(db).GetCandidatesAsync(PlatformId, EnrichmentScope.Tracked);

        candidates.ShouldBe([new EnrichmentCandidate(2, "Alpha", false), new EnrichmentCandidate(1, "Beta", true)]);
    }

    [Fact]
    public async Task TitleMediaListAsync_ReturnsRowsOrderedByIdWithoutLoadingTitle()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = NewSha256(1), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.TitleMedia.AddRange(
            new TitleMediaEntity
            {
                Id = 7,
                TitleId = 1,
                FileId = 1,
                Type = "Screenshot",
                SourceId = "gallery:igdb",
                IsPrimary = false,
                SourceUrl = "https://example.test/shot.jpg",
                Attribution = "Artist",
                SourcePageUrl = "https://example.test/page",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new TitleMediaEntity
            {
                Id = 3,
                TitleId = 1,
                FileId = 1,
                Type = "Cover",
                SourceId = "user",
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new TitleMediaEntity
            {
                Id = 5,
                TitleId = 2,
                FileId = 1,
                Type = "Cover",
                SourceId = "user",
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        ITitleMediaReader reader = new TitleReadRepository(db);

        var media = await reader.ListAsync(1);

        media.ShouldBe([
            new TitleMediaSummary(3, MediaType.Cover, 1, "user", null, null, null, true),
            new TitleMediaSummary(7, MediaType.Screenshot, 1, "gallery:igdb", "https://example.test/shot.jpg",
                "Artist", "https://example.test/page", false)
        ]);
        (await reader.ListAsync(99)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_CountsOnlyTrackedTitles()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 2, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 3, isTracked: false, status: EnrichmentStatus.Completed);
        var repository = new TitleReadRepository(db);

        var stats = await repository.GetStatsAsync();

        stats.Completed.ShouldBe(2);
    }

    /// <summary>A detached scalar-only snapshot: no children loaded and no rating state.</summary>
    private static async Task<Title> ScalarSnapshotAsync(RomdDbContext db, int titleId) =>
        (await db.Titles.AsNoTracking().SingleAsync(title => title.Id == titleId)).ToDomain();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetDetailAsync_SameContentInTwoDats_CollapsesToOneReleaseWithBothSources()
    {
        var sharedSha1 = NewSha1(0xAA);
        var uniqueSha1 = NewSha1(0xBB);
        using (var db = CreateDb())
        {
            await AddTitleAsync(db, id: 10, isTracked: false);
            SeedDatGame(db, datFileId: 1, gameId: 1, romId: 1, titleId: 10, sharedSha1, gameName: "Game (USA) (Rev 2)");
            SeedDatGame(db, datFileId: 2, gameId: 2, romId: 2, titleId: 10, sharedSha1, gameName: "Game (USA) (Rev 2)");
            SeedDatGame(db, datFileId: 1, gameId: 3, romId: 3, titleId: 10, uniqueSha1, gameName: "Game (USA)");
            await db.SaveChangesAsync();
            await BuildCatalogAsync(db);
        }

        using (var db = CreateDb())
        {
            var detail = await new TitleDetailReader(db).GetDetailAsync(10);

            detail.ShouldNotBeNull();
            // Three source games, but the two identical-content entries collapse to one release.
            detail.Releases.Count.ShouldBe(2);
            var collapsed = detail.Releases.Single(release => release.Sources.Count == 2);
            collapsed.Sources.Select(source => source.DatGameId).ShouldBe([1, 2], ignoreOrder: true);
            collapsed.Sources.Select(source => source.DatFileId).ShouldBe([1, 2], ignoreOrder: true);
            detail.Releases.ShouldContain(release => release.Sources.Count == 1);
        }
    }

    [Fact]
    public async Task GetDetailAsync_PendingVersionSharesEntry_DoesNotInheritOrDropRelease()
    {
        using (var db = CreateDb())
        {
            await AddTitleAsync(db, id: 30, isTracked: false);
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            db.Files.AddRange(
                new FileEntityPersistence
                {
                    Id = 1,
                    Sha256 = NewSha256(1),
                    Size = 1,
                    SizeOnDisk = 1,
                    IsCompressed = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new FileEntityPersistence
                {
                    Id = 2,
                    Sha256 = NewSha256(2),
                    Size = 1,
                    SizeOnDisk = 1,
                    IsCompressed = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            db.DatFiles.AddRange(
                new DatFileEntity
                {
                    Id = 1,
                    Name = "DAT v2",
                    Description = "DAT v2",
                    Type = "NoIntro",
                    PlatformId = PlatformId,
                    OriginalFilename = "v2.dat",
                    FileId = 1,
                    GameCount = 1,
                    Lifecycle = nameof(DatFileLifecycle.Active),
                    CreatedAt = now,
                    CreatedByUserId = userId,
                    Source = new DatSourceEntity
                    {
                        Id = 1,
                        CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
                    }
                },
                new DatFileEntity
                {
                    Id = 2,
                    Name = "DAT v3",
                    Description = "DAT v3",
                    Type = "NoIntro",
                    PlatformId = PlatformId,
                    OriginalFilename = "v3.dat",
                    FileId = 2,
                    GameCount = 1,
                    Lifecycle = nameof(DatFileLifecycle.PendingActivation),
                    DatSourceId = 1,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = 1,
                CatalogSourceId = 1,
                EntryKey = "Game (USA)",
                Name = "Game (USA)",
                PlatformId = PlatformId,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            // The pending replacement's game shares the entry and deliberately takes the LOWER
            // game id: a regression that groups by entry alone would make it the representative.
            db.DatGames.AddRange(
                new DatGameEntity
                {
                    Id = 1,
                    DatFileId = 2,
                    SourceEntryId = 1,
                    Name = "Game (USA)",
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatGameEntity
                {
                    Id = 2,
                    DatFileId = 1,
                    SourceEntryId = 1,
                    Name = "Game (USA)",
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity
            {
                SourceEntryId = 1,
                TitleId = 30,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            db.CatalogReleases.Add(CatalogRelease(id: 500, titleId: 30));
            db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
            {
                Id = 900,
                CatalogReleaseId = 500,
                SourceEntryId = 1,
                ProviderClaimKey = "2",
                AssertedTitleId = 30
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var detail = await new TitleDetailReader(db).GetDetailAsync(30);

            detail.ShouldNotBeNull();
            // The pending game must not collapse into the release by mere entry-sharing: it
            // stays its own unprojected row while the asserting game carries the release.
            detail.Releases.Count.ShouldBe(2);
            var pendingRow = detail.Releases.Single(release => release.Id == 1);
            pendingRow.CatalogReleaseId.ShouldBeNull();
            pendingRow.Sources.ShouldHaveSingleItem().DatFileId.ShouldBe(2);
            var projected = detail.Releases.Single(release => release.Id == 2);
            projected.CatalogReleaseId.ShouldBe(500);
            projected.Sources.ShouldHaveSingleItem().DatFileId.ShouldBe(1);
        }
    }

    [Fact]
    public async Task GetDetailAsync_PopulatesMediaSizesFromCas()
    {
        using (var db = CreateDb())
        {
            await AddTitleAsync(db, id: 20, isTracked: false);
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            db.Files.Add(new FileEntityPersistence
            {
                Id = 50,
                Sha256 = NewSha256(50),
                Size = 8000,
                SizeOnDisk = 2000,
                IsCompressed = true,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            db.TitleMedia.Add(new TitleMediaEntity
            {
                Id = 1,
                TitleId = 20,
                Type = "Cover",
                FileId = 50,
                SourceId = "user",
                IsPrimary = true,
                ContentType = "image/jpeg",
                CreatedAt = now,
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var detail = await new TitleDetailReader(db).GetDetailAsync(20);

            detail.ShouldNotBeNull();
            var media = detail.Media.ShouldHaveSingleItem();
            media.SizeBytes.ShouldBe(8000);
            media.SizeOnDiskBytes.ShouldBe(2000);
            media.IsCompressed.ShouldBe(true);
        }
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static async Task StageAndCommitAsync(RomdDbContext db, Func<Task> stage)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await stage();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    ///     Title 1 with persistence-owned state, two links, provider and user layers, a rating, two
    ///     covers (1 primary) and a primary screenshot, all of which staged writes must preserve.
    /// </summary>
    private async Task SeedCuratedTitleAsync()
    {
        using var seed = CreateDb();
        await AddTitleAsync(seed, id: 1, isTracked: false, status: EnrichmentStatus.LowConfidence);
        await seed.Titles.Where(title => title.Id == 1).ExecuteUpdateAsync(setters => setters
            .SetProperty(title => title.HasLocalPayload, true)
            .SetProperty(title => title.CatalogState, TitleCatalogState.UserOnly)
            .SetProperty(title => title.ScreenshotPrefsJson, "{\"preserve\":true}")
            .SetProperty(title => title.FieldSourceOverridesJson, "{\"Genre\":\"user\"}")
            .SetProperty(title => title.Description, "Curated description"));
        for (var id = 1; id <= 3; id++)
            seed.Files.Add(new FileEntityPersistence { Id = id, Sha256 = NewSha256((byte)id), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        seed.TitleMedia.AddRange(
            new TitleMediaEntity { Id = 1, TitleId = 1, FileId = 1, Type = "Cover", SourceId = "igdb", IsPrimary = true, Attribution = "Artist", CreatedAt = DateTimeOffset.UtcNow },
            new TitleMediaEntity { Id = 2, TitleId = 1, FileId = 2, Type = "Cover", SourceId = "user", CreatedAt = DateTimeOffset.UtcNow },
            new TitleMediaEntity { Id = 3, TitleId = 1, FileId = 3, Type = "Screenshot", SourceId = "igdb", IsPrimary = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.TitleExternalIds.AddRange(
            new TitleExternalIdEntity { TitleId = 1, Provider = "igdb", ExternalId = "123", MatchConfidence = 0.5f, CreatedAt = DateTimeOffset.UtcNow },
            new TitleExternalIdEntity { TitleId = 1, Provider = "steamgriddb", ExternalId = "77", MatchConfidence = 0.9f, CreatedAt = DateTimeOffset.UtcNow });
        seed.TitleMetadataLayers.AddRange(
            new TitleMetadataLayerEntity { TitleId = 1, SourceId = "igdb", SourceType = (int)MetadataSourceType.Provider, MetadataJson = "{\"Description\":\"provider\"}", CreatedAt = DateTimeOffset.UtcNow },
            new TitleMetadataLayerEntity { TitleId = 1, SourceId = "user", SourceType = (int)MetadataSourceType.User, MetadataJson = "{\"Description\":\"Curated description\"}", CreatedAt = DateTimeOffset.UtcNow });
        seed.TitleContentRatings.Add(new TitleContentRatingEntity { TitleId = 1, Board = 1, Code = "E", Designation = 1, SourceId = "igdb", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();
    }

    private static async Task AssertCuratedStatePreservedAsync(RomdDbContext verify, Guid originalRevision)
    {
        var stored = await verify.Titles.SingleAsync(title => title.Id == 1);
        stored.Revision.ShouldNotBe(originalRevision);
        stored.HasLocalPayload.ShouldBeTrue();
        stored.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        stored.ScreenshotPrefsJson.ShouldBe("{\"preserve\":true}");
        stored.FieldSourceOverridesJson.ShouldBe("{\"Genre\":\"user\"}");
        stored.Description.ShouldBe("Curated description");
        var links = await verify.TitleExternalIds.Where(link => link.TitleId == 1).OrderBy(link => link.Provider).ToListAsync();
        links.Select(link => (link.Provider, link.ExternalId, link.IsConfirmed))
            .ShouldBe([("igdb", "123", false), ("steamgriddb", "77", false)]);
        var layers = await verify.TitleMetadataLayers.Where(layer => layer.TitleId == 1).OrderBy(layer => layer.SourceId).ToListAsync();
        layers.Select(layer => layer.MetadataJson)
            .ShouldBe(["{\"Description\":\"provider\"}", "{\"Description\":\"Curated description\"}"]);
        layers.ShouldAllBe(layer => layer.CreatedAt > DateTimeOffset.UnixEpoch);
        var media = await verify.TitleMedia.Where(row => row.TitleId == 1).ToListAsync();
        media.Count.ShouldBe(3);
        media.Single(row => row.Id == 1).Attribution.ShouldBe("Artist");
        (await verify.TitleContentRatings.SingleAsync(rating => rating.TitleId == 1)).Code.ShouldBe("E");
    }

    private static async Task BuildCatalogAsync(RomdDbContext db)
    {
        var service = CatalogProjectionTestFactory.Create(db);
        await service.RebuildPlatformAsync(PlatformId);
    }

    private static void SeedDatGame(
        RomdDbContext db,
        int datFileId,
        int gameId,
        int romId,
        int titleId,
        Sha1 sha1,
        string gameName,
        int? datSourceId = null,
        DatFileLifecycle lifecycle = DatFileLifecycle.Active)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

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
            var datFile = new DatFileEntity
            {
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
                Lifecycle = lifecycle.ToString(),
                CreatedAt = now,
                CreatedByUserId = userId
            };
            if (datSourceId is int sourceId)
            {
                datFile.DatSourceId = sourceId;
            }
            else
            {
                // DatSource and CatalogSource share the datFileId, so entries seeded for a
                // later version of the same source can reference the catalog id directly.
                datFile.Source = new DatSourceEntity
                {
                    Id = datFileId,
                    CatalogSource = new CatalogSourceEntity { Id = datFileId, Kind = "Dat", Status = "Active" }
                };
            }

            db.DatFiles.Add(datFile);
        }

        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = gameId,
            CatalogSourceId = datSourceId ?? datFileId,
            EntryKey = gameName,
            Name = gameName,
            PlatformId = PlatformId,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = datFileId,
            SourceEntryId = gameId,
            Name = gameName,
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = romId,
            DatGameId = gameId,
            Name = $"{gameName}.sfc",
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

    private static void SeedLinkedEntry(RomdDbContext db, int entryId, int titleId)
    {
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = entryId,
            Kind = "Dat",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = entryId,
            CatalogSourceId = entryId,
            EntryKey = $"Entry {entryId}",
            Name = $"Entry {entryId}",
            PlatformId = PlatformId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = entryId,
            TitleId = titleId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static CatalogReleaseEntity CatalogRelease(int id, int titleId) => new()
    {
        Id = id,
        PlatformId = PlatformId,
        CatalogTitleId = titleId,
        Fingerprint = $"sha1:{id}",
        Name = $"Release {id}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CreatedByUserId = Guid.NewGuid()
    };

    private static CatalogReleaseSourceEntity ReleaseSource(
        int id,
        int releaseId,
        int entryId,
        int assertedTitleId) => new()
        {
            Id = id,
            CatalogReleaseId = releaseId,
            SourceEntryId = entryId,
            ProviderClaimKey = "1",
            AssertedTitleId = assertedTitleId
        };

    /// <summary>
    ///     DeleteOrRetainAsync requires a caller-owned transaction (its mutations span
    ///     multiple statements); this mirrors the production caller shape.
    /// </summary>
    private static async Task<bool> DeleteOrRetainInCallerTransactionAsync(RomdDbContext db, int titleId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        bool deleted = await new TitleOrphanRepository(db).DeleteOrRetainAsync(titleId);
        await transaction.CommitAsync();
        return deleted;
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private async Task<bool> GetTrackedAsync(int titleId)
    {
        using var db = CreateDb();
        return await db.TrackedTitles.AnyAsync(t => t.TitleId == titleId);
    }

    private static void SeedPlatform(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Super Nintendo Entertainment System",
            BaseCompactLabel = "Super Nintendo Entertainment System",
            CanonicalKey = "snes",
            ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static async Task AddTitleAsync(
        RomdDbContext db,
        int id,
        bool isTracked,
        EnrichmentStatus status = EnrichmentStatus.None,
        string? name = null)
    {
        db.Titles.Add(new TitleEntity
        {
            Id = id,
            PlatformId = PlatformId,
            Name = name ?? $"Title {id}",
            NormalizedName = $"title{id}",
            EnrichmentStatus = status.ToString(),
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        if (isTracked)
        {
            db.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
