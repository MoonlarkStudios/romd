using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleRepositoryTests : IDisposable
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
    public async Task AddUserMedia_RetryDeduplicatesByContentAndType()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = NewSha256(1), Size = 10, SizeOnDisk = 10, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        foreach (var type in new[] { MediaType.Screenshot, MediaType.Screenshot, MediaType.Cover })
        {
            using var attempt = CreateDb();
            await using var transaction = await attempt.Database.BeginTransactionAsync();
            await new TitleRepository(attempt).AddUserMediaStagedAsync(TitleMedia.CreateNew(1, type, 1, "user", "image/png"));
            await attempt.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var media = await db.TitleMedia.Where(item => item.TitleId == 1).ToListAsync();
        media.Count.ShouldBe(2);
        media.Count(item => item.Type == "Screenshot").ShouldBe(1);
        media.ShouldAllBe(item => item.IsPrimary);
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
        var first = TitleMedia.CreateNew(1, MediaType.Screenshot, 1, "gallery:igdb", "image/png", "https://images.igdb.com/first.png");
        first.SetAttribution("Artist", "https://www.igdb.com/games/test");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await repository.AppendGalleryMediaStagedAsync(first);
            await db.SaveChangesAsync();
            await repository.AppendGalleryMediaStagedAsync(first);
            await repository.AppendGalleryMediaStagedAsync(TitleMedia.CreateNew(1, MediaType.Screenshot, 2, "gallery:igdb", "image/png"));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var title = (await repository.GetWithMetadataLayersAsync(1))!;
        title.Media.Count.ShouldBe(2);
        title.Media.ShouldAllBe(media => !media.IsPrimary);
        title.Media.Single(media => media.FileId == 1).Attribution.ShouldBe("Artist");
        title.StoreUserLayer(new TitleMetadataPayload { Description = "Updated" });
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
    public async Task MarkTrackedAsync_SetsTrackedTrue_AndIsIdempotent()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: true);
        var repository = new TitleRepository(db);

        await repository.MarkTrackedAsync([1, 2]);

        (await GetTrackedAsync(1)).ShouldBeTrue();
        (await GetTrackedAsync(2)).ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_DetachedDomainTitle_PreservesProjectionOwnedHasLocalPayload()
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
        var detached = await repository.GetByIdAsync(1);
        detached.ShouldNotBeNull();
        detached.QueueForEnrichment();

        await repository.UpdateAsync(detached);

        var persisted = await db.Titles.AsNoTracking().SingleAsync(title => title.Id == 1);
        persisted.HasLocalPayload.ShouldBeTrue();
        persisted.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        persisted.ScreenshotPrefsJson.ShouldBe("{\"preserve\":true}");
        persisted.FieldProvenanceJson.ShouldBe("{\"Description\":\"igdb\"}");
        persisted.FieldSourceOverridesJson.ShouldBe("{\"Description\":\"igdb\"}");
    }

    [Theory]
    [InlineData("immediate")]
    [InlineData("staged")]
    [InlineData("materialized")]
    [InlineData("user")]
    public async Task UpdateAsync_ConcurrentEnrichment_RejectsStaleSnapshotAndRollsBackChildren(string route)
    {
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 1, isTracked: false);
            seed.Files.Add(new FileEntityPersistence
            {
                Id = 1, Sha256 = NewSha256(1), Size = 10, SizeOnDisk = 10,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
            seed.TitleMedia.Add(new TitleMediaEntity
            {
                Id = 1, TitleId = 1, FileId = 1, Type = "Cover", SourceId = "igdb",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        using var staleDb = CreateDb();
        using var winnerDb = CreateDb();
        var staleRepository = new TitleRepository(staleDb);
        var winnerRepository = new TitleRepository(winnerDb);
        var stale = (await staleRepository.GetWithMetadataLayersAsync(1))!;
        var winner = (await winnerRepository.GetWithMetadataLayersAsync(1))!;
        var initialRevision = stale.Revision;
        initialRevision.ShouldNotBe(Guid.Empty);

        winner.StoreProviderLayer("igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "Concurrent provider description" });
        winner.Rematerialize(["igdb"]);
        await winnerRepository.UpdateAsync(winner);

        stale.RemoveMedia(1).ShouldBeTrue();
        stale.StoreUserLayer(new TitleMetadataPayload { Description = "Stale user description" });
        stale.Rematerialize(["igdb"]);
        if (route == "immediate")
        {
            await Should.ThrowAsync<PersistenceConflictException>(() => staleRepository.UpdateAsync(stale));
        }
        else
        {
            await using var transaction = await staleDb.Database.BeginTransactionAsync();
            await Should.ThrowAsync<PersistenceConflictException>(async () =>
            {
                if (route == "staged")
                    await staleRepository.UpdateStagedAsync(stale);
                else if (route == "materialized")
                    await staleRepository.UpdateMaterializedMetadataStagedAsync(stale);
                else
                    await staleRepository.UpdateUserMetadataStagedAsync(stale);
                await staleDb.SaveChangesAsync();
            });
            await transaction.RollbackAsync();
        }

        using var verify = CreateDb();
        var stored = (await new TitleRepository(verify).GetWithMetadataLayersAsync(1))!;
        stored.Media.ShouldContain(media => media.Id == 1);
        stored.Revision.ShouldNotBe(initialRevision);
        stored.Description.ShouldBe("Concurrent provider description");
        stored.GetMetadataLayer("user").ShouldBeNull();
        stored.GetMetadataLayer("igdb")!.GetPayload()!.Description.ShouldBe("Concurrent provider description");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_ConcurrentFirstUserLayers_ReturnsConflictWithoutReplacingWinner(bool staged)
    {
        using (var seed = CreateDb())
        {
            await AddTitleAsync(seed, id: 1, isTracked: false);
        }
        using var firstDb = CreateDb();
        using var staleDb = CreateDb();
        var firstRepository = new TitleRepository(firstDb);
        var staleRepository = new TitleRepository(staleDb);
        var first = (await firstRepository.GetWithMetadataLayersAsync(1))!;
        var stale = (await staleRepository.GetWithMetadataLayersAsync(1))!;
        first.StoreUserLayer(new TitleMetadataPayload { Description = "Winner" });
        first.Rematerialize([]);
        await firstRepository.UpdateAsync(first);
        stale.StoreUserLayer(new TitleMetadataPayload { Description = "Stale" });
        stale.Rematerialize([]);

        if (staged)
        {
            await using var transaction = await staleDb.Database.BeginTransactionAsync();
            await Should.ThrowAsync<PersistenceConflictException>(async () =>
            {
                await staleRepository.UpdateUserMetadataStagedAsync(stale);
                await staleDb.SaveChangesAsync();
            });
            await transaction.RollbackAsync();
        }
        else
        {
            await Should.ThrowAsync<PersistenceConflictException>(() => staleRepository.UpdateAsync(stale));
        }

        using var verify = CreateDb();
        var stored = (await new TitleRepository(verify).GetWithMetadataLayersAsync(1))!;
        stored.Description.ShouldBe("Winner");
        stored.MetadataLayers.Count.ShouldBe(1);
        stored.GetMetadataLayer("user")!.GetPayload()!.Description.ShouldBe("Winner");
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
                TitleId = 1, SourceId = "igdb", SourceType = (int)MetadataSourceType.Provider,
                MetadataJson = "{\"Description\":\"provider\"}", CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        using var db = CreateDb();
        var repository = new TitleRepository(db);
        var title = (await repository.GetWithMetadataLayersAsync(1))!;
        var revision = title.Revision;
        title.StoreUserLayer(new TitleMetadataPayload { Description = "User description" });
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
    public async Task MarkTrackedAsync_EmptyInput_IsNoOp()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        var repository = new TitleRepository(db);

        await repository.MarkTrackedAsync([]);

        (await GetTrackedAsync(1)).ShouldBeFalse();
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = "n64", ShortName = "n64",
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
    public async Task DeleteAsync_TrackedTitle_RetainsUserOnlyTitle()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true);

        await new TitleRepository(db).DeleteAsync(1);

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
            deleted = await new TitleRepository(db).DeleteOrRetainAsync(1);
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
            () => new TitleRepository(db).DeleteOrRetainAsync(1));

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
    public async Task GetNeedingEnrichmentByPlatformAsync_TrackedScope_ExcludesUntracked()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.None);
        await AddTitleAsync(db, id: 2, isTracked: false, status: EnrichmentStatus.None);
        var repository = new TitleRepository(db);

        var result = await repository.GetNeedingEnrichmentByPlatformAsync(PlatformId, EnrichmentScope.Tracked);

        result.Select(t => t.Id).ShouldBe([1]);
    }

    [Fact]
    public async Task GetNeedingEnrichmentByPlatformAsync_AllScope_IncludesUntracked()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.None);
        await AddTitleAsync(db, id: 2, isTracked: false, status: EnrichmentStatus.None);
        var repository = new TitleRepository(db);

        var result = await repository.GetNeedingEnrichmentByPlatformAsync(PlatformId, EnrichmentScope.All);

        result.Select(t => t.Id).OrderBy(id => id).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task GetNeedingEnrichmentByPlatformAsync_ExcludesCompletedAndNotFound()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 2, isTracked: true, status: EnrichmentStatus.NotFound);
        await AddTitleAsync(db, id: 3, isTracked: true, status: EnrichmentStatus.LowConfidence);
        var repository = new TitleRepository(db);

        var result = await repository.GetNeedingEnrichmentByPlatformAsync(PlatformId, EnrichmentScope.All);

        result.Select(t => t.Id).ShouldBe([3]);
    }

    [Fact]
    public async Task GetEnrichmentStatsAsync_CountsOnlyTrackedTitles()
    {
        using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 2, isTracked: true, status: EnrichmentStatus.Completed);
        await AddTitleAsync(db, id: 3, isTracked: false, status: EnrichmentStatus.Completed);
        var repository = new TitleRepository(db);

        var stats = await repository.GetEnrichmentStatsAsync();

        stats.Completed.ShouldBe(2);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetTitleDetailAsync_SameContentInTwoDats_CollapsesToOneReleaseWithBothSources()
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
            var detail = await new TitleRepository(db).GetTitleDetailAsync(10);

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
    public async Task GetTitleDetailAsync_PendingVersionSharesEntry_DoesNotInheritOrDropRelease()
    {
        using (var db = CreateDb())
        {
            await AddTitleAsync(db, id: 30, isTracked: false);
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            db.Files.AddRange(
                new FileEntityPersistence
                {
                    Id = 1, Sha256 = NewSha256(1), Size = 1, SizeOnDisk = 1, IsCompressed = false,
                    CreatedAt = now, CreatedByUserId = userId
                },
                new FileEntityPersistence
                {
                    Id = 2, Sha256 = NewSha256(2), Size = 1, SizeOnDisk = 1, IsCompressed = false,
                    CreatedAt = now, CreatedByUserId = userId
                });
            db.DatFiles.AddRange(
                new DatFileEntity
                {
                    Id = 1, Name = "DAT v2", Description = "DAT v2", Type = "NoIntro",
                    PlatformId = PlatformId, OriginalFilename = "v2.dat", FileId = 1,
                    GameCount = 1, Lifecycle = nameof(DatFileLifecycle.Active),
                    CreatedAt = now, CreatedByUserId = userId,
                    Source = new DatSourceEntity
                    {
                        Id = 1,
                        CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
                    }
                },
                new DatFileEntity
                {
                    Id = 2, Name = "DAT v3", Description = "DAT v3", Type = "NoIntro",
                    PlatformId = PlatformId, OriginalFilename = "v3.dat", FileId = 2,
                    GameCount = 1, Lifecycle = nameof(DatFileLifecycle.PendingActivation),
                    DatSourceId = 1, CreatedAt = now, CreatedByUserId = userId
                });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = 1, CatalogSourceId = 1, EntryKey = "Game (USA)", Name = "Game (USA)",
                PlatformId = PlatformId, CreatedAt = now, CreatedByUserId = userId
            });
            // The pending replacement's game shares the entry and deliberately takes the LOWER
            // game id: a regression that groups by entry alone would make it the representative.
            db.DatGames.AddRange(
                new DatGameEntity
                {
                    Id = 1, DatFileId = 2, SourceEntryId = 1, Name = "Game (USA)",
                    CreatedAt = now, CreatedByUserId = userId
                },
                new DatGameEntity
                {
                    Id = 2, DatFileId = 1, SourceEntryId = 1, Name = "Game (USA)",
                    CreatedAt = now, CreatedByUserId = userId
                });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity
            {
                SourceEntryId = 1, TitleId = 30, CreatedAt = now, CreatedByUserId = userId
            });
            db.CatalogReleases.Add(CatalogRelease(id: 500, titleId: 30));
            db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
            {
                Id = 900, CatalogReleaseId = 500, SourceEntryId = 1, ProviderClaimKey = "2",
                AssertedTitleId = 30
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var detail = await new TitleRepository(db).GetTitleDetailAsync(30);

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
    public async Task GetTitleDetailAsync_PopulatesMediaSizesFromCas()
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
            var detail = await new TitleRepository(db).GetTitleDetailAsync(20);

            detail.ShouldNotBeNull();
            var media = detail.Media.ShouldHaveSingleItem();
            media.SizeBytes.ShouldBe(8000);
            media.SizeOnDiskBytes.ShouldBe(2000);
            media.IsCompressed.ShouldBe(true);
        }
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

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
        bool deleted = await new TitleRepository(db).DeleteOrRetainAsync(titleId);
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo Entertainment System", BaseCompactLabel = "Super Nintendo Entertainment System", CanonicalKey = "snes", ShortName = "snes",
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
