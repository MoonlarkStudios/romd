using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ArtworkTitleMergeTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task StageAsync_NoCallerTransaction_RejectsWithoutMutation()
    {
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => new ArtworkTitleMerger(db).StageAsync(1, 2));
    }

    [Fact]
    public async Task StageAsync_SourcePinAndPending_TransfersEffectivePinAndInvalidatesSourceWorker()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.Add(Asset(1, 1, "asset-a"));
        var source = Selection(1, 1, 8, Guid.NewGuid());
        source.FocalX = 0;
        source.FocalY = 100;
        db.ArtworkSelections.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var stale = _database.CreateContext();
        var sourceIntent = await stale.ArtworkSelections.AsTracking().SingleAsync();

        await MergeAsync(db);

        var selection = await db.ArtworkSelections.SingleAsync();
        selection.TitleId.ShouldBe(2);
        selection.PinnedAssetId.ShouldBe(1);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        selection.Revision.ShouldBe(9);
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
        selection.PendingRequestId.ShouldBeNull();
        (await db.ArtworkAssets.SingleAsync()).TitleId.ShouldBe(2);
        (await db.ArtworkVariants.CountAsync()).ShouldBe(1);
        sourceIntent.PendingRequestId = null;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_TargetPinOrPending_WinsOverSourcePin(bool pending)
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        var request = Guid.NewGuid();
        db.ArtworkAssets.AddRange(Asset(1, 1, "asset-a"), Asset(2, 2, "asset-b"));
        db.ArtworkSelections.AddRange(Selection(1, 1, 8), Selection(2, pending ? null : 2, 3,
            pending ? request : null));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await MergeAsync(db);

        var selection = await db.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(pending ? null : 2);
        selection.PendingRequestId.ShouldBe(pending ? request : null);
        selection.Revision.ShouldBe(3);
        (await db.ArtworkAssets.CountAsync(asset => asset.TitleId == 2)).ShouldBe(2);
    }

    [Fact]
    public async Task StageAsync_ExactDuplicate_RemapSourcePinButRetainOtherProviderCandidatesAndRevisions()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.AddRange(Asset(1, 1, "asset-a"), Asset(2, 2, "asset-a"),
            Asset(3, 1, "asset-b"), Asset(4, 1, "asset-a", "v2"));
        var source = Selection(1, 1, 8);
        source.FocalX = 25;
        source.FocalY = 75;
        db.ArtworkSelections.AddRange(source, Selection(2, null, 20));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await MergeAsync(db);

        var selection = await db.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(2);
        selection.Revision.ShouldBe(21);
        selection.FocalX.ShouldBe(25);
        selection.FocalY.ShouldBe(75);
        (await db.ArtworkAssets.OrderBy(asset => asset.Id).Select(asset => asset.Id).ToListAsync())
            .ShouldBe([2, 3, 4]);
        (await db.ArtworkAssets.AllAsync(asset => asset.TitleId == 2)).ShouldBeTrue();
        (await db.ArtworkVariants.CountAsync()).ShouldBe(3);
        (await db.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task StageAsync_ReadySourceDuplicate_RepairsIncompleteTargetAndPreservesTargetPinIdentity()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        var incomplete = Asset(2, 2, "asset-a");
        incomplete.IsEligible = false;
        incomplete.Variants.Clear();
        db.ArtworkAssets.AddRange(Asset(1, 1, "asset-a"), incomplete);
        db.ArtworkSelections.AddRange(Selection(1, 1, 8), Selection(2, 2, 3));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await MergeAsync(db);

        var asset = await db.ArtworkAssets.Include(candidate => candidate.Variants).SingleAsync();
        asset.Id.ShouldBe(2);
        asset.IsEligible.ShouldBeTrue();
        asset.Variants.Single().AssetId.ShouldBe(2);
        asset.ToDomain().Variants.Single().Width.ShouldBe(300);
        var selection = await db.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(2);
        selection.Revision.ShouldBe(3);
    }

    [Fact]
    public async Task StageAsync_RolledBack_RestoresAssetsAndSourceSelection()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        db.ArtworkAssets.Add(Asset(1, 1, "asset-a"));
        db.ArtworkSelections.Add(Selection(1, 1, 8));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new ArtworkTitleMerger(db).StageAsync(1, 2);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        (await db.ArtworkSelections.SingleAsync()).TitleId.ShouldBe(1);
        (await db.ArtworkAssets.SingleAsync()).TitleId.ShouldBe(1);
    }

    private static async Task MergeAsync(RomdDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new ArtworkTitleMerger(db).StageAsync(1, 2);
        await db.SaveChangesAsync();
        await db.Titles.Where(title => title.Id == 1).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
    }

    private static ArtworkSelectionEntity Selection(int titleId, int? assetId, long revision, Guid? pending = null) => new()
    {
        TitleId = titleId, Role = ArtworkRole.Poster, PinnedAssetId = assetId,
        Mode = assetId is null ? ArtworkSelectionMode.Automatic : ArtworkSelectionMode.Pinned,
        Revision = revision, PendingRequestId = pending
    };

    private static ArtworkAssetEntity Asset(int id, int titleId, string providerAssetId, string version = "v1") => new()
    {
        Id = id, TitleId = titleId, Role = ArtworkRole.Poster, SourceId = "steamgriddb",
        ProviderAssetId = providerAssetId, ContentVersion = version, OriginalFileId = 1,
        ContentType = "image/png", Width = 600, Height = 900, IsEligible = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Variants = [new ArtworkVariantEntity
        {
            AssetId = id, Name = "card", FileId = 1, ContentVersion = version,
            ContentType = "image/png", Width = 300, Height = 450
        }]
    };

    private static async Task SeedAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1, Name = "Platform", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform", BaseCompactLabel = "Platform", CanonicalKey = "platform", ShortName = "platform", Manufacturer = "Test",
            CreatedAt = now, CreatedByUserId = user
        });
        foreach (var id in new[] { 1, 2 })
            db.Titles.Add(new TitleEntity
            {
                Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"title {id}",
                EnrichmentStatus = "None", FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}", CreatedAt = now, CreatedByUserId = user
            });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 1, Sha256 = Sha256.FromBytes(new byte[Sha256.ByteLength]), Size = 100,
            SizeOnDisk = 100, CreatedAt = now, CreatedByUserId = user
        });
        await db.SaveChangesAsync();
    }

    public void Dispose() => _database.Dispose();
}
