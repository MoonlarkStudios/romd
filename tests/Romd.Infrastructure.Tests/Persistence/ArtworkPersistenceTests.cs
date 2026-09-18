using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ArtworkPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task FileReferences_RetainedOriginalAndVariants_AreMediaAndNeverOrphans()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.Add(Asset(1, "steamgriddb", "grid-a"));
        await db.SaveChangesAsync();
        var repository = new FileRepository(db);

        foreach (var fileId in new[] { 1, 2, 3 })
            (await repository.IsReferencedAsync(fileId, CancellationToken.None)).ShouldBeTrue();
        (await repository.IsReferencedAsync(4, CancellationToken.None)).ShouldBeFalse();
        (await repository.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None)).ShouldBe([4]);
        var stats = await repository.GetStorageStatsAsync(CancellationToken.None);
        var media = stats.Breakdown.Single(x => x.Category == StorageCategories.Media);
        media.FileCount.ShouldBe(3);
        media.Size.ShouldBe(600);
        media.SizeOnDisk.ShouldBe(600);
        stats.Breakdown.Single(x => x.Category == StorageCategories.Unattributed).Size.ShouldBe(400);
    }

    [Fact]
    public async Task SaveChanges_MultipleProviderAssetsAndRevisionsAllowed_DuplicateRevisionRejected()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.AddRange(Asset(1, "steamgriddb", "grid-a"), Asset(2, "steamgriddb", "grid-b"));
        var updated = Asset(3, "steamgriddb", "grid-a");
        updated.ContentVersion = "new-original-content-v1";
        db.ArtworkAssets.Add(updated);
        await db.SaveChangesAsync();
        (await db.ArtworkAssets.CountAsync()).ShouldBe(3);
        db.ArtworkAssets.Add(Asset(4, "steamgriddb", "grid-a"));

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData(2, ArtworkRole.Poster)]
    [InlineData(1, ArtworkRole.Hero)]
    public async Task SaveChanges_PinForAnotherTitleOrRole_RejectsForeignKey(int titleId, ArtworkRole role)
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.Add(Asset(1, "steamgriddb", "grid-a"));
        await db.SaveChangesAsync();
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = titleId, Role = role, Mode = ArtworkSelectionMode.Pinned, PinnedAssetId = 1, Revision = 1
        });

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task ResolveAsync_PreferencesAndHistoricalCover_ProduceSharedMeasuredAndContainedContracts()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.AddRange(Asset(1, "igdb", "cover-a"), Asset(2, "steamgriddb", "grid-a"),
            Asset(3, "igdb", "hero-a", ArtworkRole.Hero), Asset(4, "steamgriddb", "logo-a", ArtworkRole.Logo));
        db.ArtworkPreferences.AddRange(
            new ArtworkPreferenceEntity { Role = ArtworkRole.Poster, SourceId = "steamgriddb", Priority = 0 },
            new ArtworkPreferenceEntity { Role = ArtworkRole.Poster, SourceId = "igdb", Priority = 1 });
        db.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 1, TitleId = 2, Type = "Cover", FileId = 4, SourceId = "user", IsPrimary = true,
            ContentType = "image/jpeg", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
        var reader = new ArtworkReader(db);

        var resolved = await reader.ResolveAsync([1, 2]);
        var poster = resolved[1].Single(x => x.Role == ArtworkRole.Poster).ToContract();
        var hero = resolved[1].Single(x => x.Role == ArtworkRole.Hero).ToContract();
        var logo = resolved[1].Single(x => x.Role == ArtworkRole.Logo).ToContract();
        var box = resolved[2].Single(x => x.Role == ArtworkRole.Poster).ToContract();
        var missingHero = resolved[2].Single(x => x.Role == ArtworkRole.Hero).ToContract();
        var missingLogo = resolved[2].Single(x => x.Role == ArtworkRole.Logo).ToContract();

        poster.AssetId.ShouldBe(IdCoder.Encode(2));
        poster.Width.ShouldBe(300);
        poster.Height.ShouldBe(450);
        poster.OriginalWidth.ShouldBe(600);
        poster.OriginalHeight.ShouldBe(900);
        poster.Fit.ShouldBe("Contain");
        poster.Variants.Count.ShouldBe(2);
        poster.Url.ShouldBe($"/artwork/{IdCoder.Encode(2)}/card/card-content-v1");
        poster.ContentVersion.ShouldBe("card-content-v1");
        hero.AssetId.ShouldBe(IdCoder.Encode(3));
        hero.OriginalWidth.ShouldBe(1920);
        hero.OriginalHeight.ShouldBe(620);
        logo.AssetId.ShouldBe(IdCoder.Encode(4));
        logo.OriginalWidth.ShouldBe(1200);
        logo.OriginalHeight.ShouldBe(300);
        logo.Width.ShouldBe(600);
        logo.Height.ShouldBe(150);
        logo.Fit.ShouldBe("Contain");
        box.Url.ShouldBe($"/media/{IdCoder.Encode(1)}");
        box.Fit.ShouldBe("Contain");
        box.FallbackReason.ShouldBe("LegacyCover");
        box.Width.ShouldBeNull();
        box.OriginalWidth.ShouldBeNull();
        missingHero.Url.ShouldBeNull();
        missingHero.FallbackReason.ShouldBe("NoArtwork");
        missingLogo.Url.ShouldBeNull();
        missingLogo.FallbackReason.ShouldBe("NoArtwork");
        (await reader.FindVariantAsync(2, "card", "card-content-v1"))!.Sha256.ShouldBe(Hash(2));
        (await reader.FindVariantAsync(2, "card", "stale-content-v1")).ShouldBeNull();
        (await reader.FindVariantAsync(2, "original", "original-content-v1")).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ReopenedContextAfterPreferenceChange_PreservesPersistedPin()
    {
        await using (var db = _database.CreateContext())
        {
            await SeedAsync(db);
            db.ArtworkAssets.AddRange(Asset(1, "igdb", "cover-a"), Asset(2, "steamgriddb", "grid-a"));
            db.ArtworkSelections.Add(new ArtworkSelectionEntity
            {
                TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Pinned,
                PinnedAssetId = 1, Revision = 8
            });
            await db.SaveChangesAsync();
            db.ArtworkPreferences.Add(new ArtworkPreferenceEntity
            {
                Role = ArtworkRole.Poster, SourceId = "steamgriddb", Priority = 0
            });
            await db.SaveChangesAsync();
        }

        await using var reopened = _database.CreateContext();
        var resolved = await new ArtworkReader(reopened).ResolveAsync([1]);

        resolved[1].Single(x => x.Role == ArtworkRole.Poster).Asset!.Id.ShouldBe(1);
        var selection = await reopened.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(1);
        selection.Revision.ShouldBe(8);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_StalePublicationAfterNewIntent_RejectsConcurrentUpdate(bool newerPin)
    {
        var pending = Guid.NewGuid();
        await using (var setup = _database.CreateContext())
        {
            await SeedAsync(setup);
            setup.ArtworkAssets.Add(Asset(1, "steamgriddb", "grid-a"));
            setup.ArtworkSelections.Add(new ArtworkSelectionEntity
            {
                TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Automatic,
                Revision = 1, PendingRequestId = pending
            });
            await setup.SaveChangesAsync();
        }
        await using var staleContext = _database.CreateContext();
        await using var currentContext = _database.CreateContext();
        var stale = await staleContext.ArtworkSelections.AsTracking().SingleAsync();
        var current = await currentContext.ArtworkSelections.AsTracking().SingleAsync();
        var nextRequest = Guid.NewGuid();
        var intent = current.ToDomain();
        if (newerPin) intent.RequestPin(nextRequest);
        else intent.ReturnToAutomatic();
        current.Revision = intent.Revision;
        current.PendingRequestId = intent.PendingRequestId;
        current.Mode = intent.Mode;
        current.PinnedAssetId = intent.PinnedAssetId;
        await currentContext.SaveChangesAsync();
        stale.Mode = ArtworkSelectionMode.Pinned;
        stale.PinnedAssetId = 1;
        stale.PendingRequestId = null;

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());

        await using var verify = _database.CreateContext();
        var saved = await verify.ArtworkSelections.SingleAsync();
        saved.Revision.ShouldBe(2);
        saved.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        saved.PinnedAssetId.ShouldBeNull();
        saved.PendingRequestId.ShouldBe(newerPin ? nextRequest : null);
    }

    [Fact]
    public async Task DeleteTitle_WithPinnedArtwork_RemovesAssociationsAndLeavesFilesForCollection()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.Add(Asset(1, "steamgriddb", "grid-a"));
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Pinned,
            PinnedAssetId = 1, Revision = 1
        });
        await db.SaveChangesAsync();

        await db.Titles.Where(x => x.Id == 1).ExecuteDeleteAsync();

        (await db.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await db.ArtworkVariants.CountAsync()).ShouldBe(0);
        (await db.ArtworkSelections.CountAsync()).ShouldBe(0);
        (await db.Files.CountAsync()).ShouldBe(4);
        var repository = new FileRepository(db);
        (await repository.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None)).Order().ShouldBe([1, 2, 3, 4]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteOrRetainAsync_OnlyArtworkStateExists_RetainsTitle(bool assetExists)
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        var requestId = Guid.NewGuid();
        if (assetExists)
            db.ArtworkAssets.Add(Asset(1, "steamgriddb", "grid-a"));
        else
            db.ArtworkSelections.Add(new ArtworkSelectionEntity
            {
                TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Automatic,
                PendingRequestId = requestId, Revision = 1
            });
        await db.SaveChangesAsync();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await new TitleRepository(db).DeleteOrRetainAsync(1)).ShouldBeFalse();
            await transaction.CommitAsync();
        }

        (await db.Titles.SingleAsync(x => x.Id == 1)).CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        if (assetExists)
        {
            (await db.ArtworkAssets.CountAsync()).ShouldBe(1);
            (await db.ArtworkVariants.CountAsync()).ShouldBe(2);
        }
        else
            (await db.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(requestId);
    }

    public void Dispose() => _database.Dispose();

    private static async Task SeedAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1, Name = "Super Nintendo", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes", Manufacturer = "Nintendo",
            CreatedAt = now, CreatedByUserId = userId
        });
        for (var id = 1; id <= 2; id++)
            db.Titles.Add(new TitleEntity
            {
                Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"title {id}",
                EnrichmentStatus = "None", FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}", CreatedAt = now, CreatedByUserId = userId
            });
        for (var id = 1; id <= 4; id++)
            db.Files.Add(new FileEntityPersistence
            {
                Id = id, Sha256 = Hash(id), Size = id * 100, SizeOnDisk = id * 100,
                CreatedAt = now, CreatedByUserId = userId
            });
        await db.SaveChangesAsync();
    }

    private static ArtworkAssetEntity Asset(int id, string source, string providerAssetId,
        ArtworkRole role = ArtworkRole.Poster) => new()
    {
        Id = id, TitleId = 1, Role = role, SourceId = source, ProviderGameId = "game-a",
        ProviderAssetId = providerAssetId, OriginalFileId = 1, ContentVersion = "original-content-v1",
        ContentType = "image/png", Width = role switch { ArtworkRole.Poster => 600, ArtworkRole.Hero => 1920, _ => 1200 },
        Height = role switch { ArtworkRole.Poster => 900, ArtworkRole.Hero => 620, _ => 300 },
        IsEligible = true, CreatedAt = DateTimeOffset.UnixEpoch,
        Variants =
        [
            new ArtworkVariantEntity
            {
                AssetId = id, Name = "card", FileId = 2, ContentVersion = "card-content-v1",
                ContentType = "image/webp", Width = role switch { ArtworkRole.Poster => 300, ArtworkRole.Hero => 960, _ => 600 },
                Height = role switch { ArtworkRole.Poster => 450, ArtworkRole.Hero => 310, _ => 150 }
            },
            new ArtworkVariantEntity
            {
                AssetId = id, Name = "thumbnail", FileId = 3, ContentVersion = "thumb-content-v1",
                ContentType = "image/webp", Width = role switch { ArtworkRole.Poster => 120, ArtworkRole.Hero => 192, _ => 240 },
                Height = role switch { ArtworkRole.Poster => 180, ArtworkRole.Hero => 62, _ => 60 }
            }
        ]
    };

    private static Sha256 Hash(int id)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = (byte)id;
        return Sha256.FromBytes(bytes);
    }
}
