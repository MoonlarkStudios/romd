using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class CatalogArtworkSearchTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task SearchTitlesAsync_ArtworkBatchesOnlyPageRows_LeavesKeysetPaginationUnchanged()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        var reader = new RecordingReader();
        var repository = new SearchRepository(db, reader);

        var first = await repository.SearchTitlesAsync(null, new TitleSearchFilters(), TitleSortField.Name, null, 2, null);
        var second = await repository.SearchTitlesAsync(null, new TitleSearchFilters(), TitleSortField.Name, first.NextCursor, 2, null);

        first.Items.Select(item => item.Id).ShouldBe([1, 2]);
        second.Items.Select(item => item.Id).ShouldBe([3]);
        first.HasNextPage.ShouldBeTrue();
        second.HasNextPage.ShouldBeFalse();
        reader.Batches.Count.ShouldBe(2);
        reader.Batches[0].ShouldBe([1, 2]);
        reader.Batches[1].ShouldBe([3]);
        first.Items.ShouldAllBe(item => item.Artwork.Count == 4);
        second.Items.Single().Artwork.Count.ShouldBe(4);
        first.Items[0].EnrichmentStatus.ShouldBe("Completed");
        first.Items[1].EnrichmentStatus.ShouldBe("Failed");
    }

    [Fact]
    public async Task SearchTitlesAsync_RealArtworkReader_UsesPinAndContainedHistoricalFallback()
    {
        await using var db = _database.CreateContext();
        await SeedAsync(db);
        var originalHash = new byte[Sha256.ByteLength];
        originalHash[0] = 1;
        var variantHash = new byte[Sha256.ByteLength];
        variantHash[0] = 2;
        db.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromBytes(originalHash), Size = 100, SizeOnDisk = 100, CreatedAt = DateTimeOffset.UtcNow },
            new FileEntityPersistence { Id = 2, Sha256 = Sha256.FromBytes(variantHash), Size = 50, SizeOnDisk = 50, CreatedAt = DateTimeOffset.UtcNow });
        db.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            Id = 10, TitleId = 1, Role = ArtworkRole.Poster, SourceId = "steamgriddb", ProviderGameId = "game", ProviderAssetId = "asset",
            OriginalFileId = 1, ContentVersion = "original", ContentType = "image/png", Width = 600, Height = 900,
            IsEligible = true, CreatedAt = DateTimeOffset.UtcNow,
            Variants = [new ArtworkVariantEntity { AssetId = 10, Name = "poster", FileId = 2, ContentVersion = "poster-v1", ContentType = "image/webp", Width = 300, Height = 450 }]
        });
        db.ArtworkSelections.Add(new ArtworkSelectionEntity { TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Pinned, PinnedAssetId = 10, Revision = 1 });
        db.TitleMedia.Add(new TitleMediaEntity { Id = 20, TitleId = 2, Type = "Cover", FileId = 1, SourceId = "igdb", IsPrimary = true, ContentType = "image/png", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = await new SearchRepository(db).SearchTitlesAsync(null, new TitleSearchFilters(), TitleSortField.Name, null, 10, null);

        page.Items.Single(item => item.Id == 1).Artwork.Single(item => item.Role == ArtworkRole.Poster).Asset!.Id.ShouldBe(10);
        var fallback = page.Items.Single(item => item.Id == 2).Artwork.Single(item => item.Role == ArtworkRole.Poster);
        fallback.Fit.ShouldBe(ArtworkFit.Contain);
        fallback.LegacyCover!.Id.ShouldBe(20);
        fallback.Asset.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private static async Task SeedAsync(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Platform", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform", BaseCompactLabel = "Platform", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test", CreatedAt = DateTimeOffset.UtcNow });
        for (var id = 1; id <= 3; id++)
            db.Titles.Add(new TitleEntity { Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"title{id}", EnrichmentStatus = id == 1 ? "Completed" : id == 2 ? "Failed" : "None", FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}", ScreenshotPrefsJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingReader : IArtworkReader
    {
        public List<int[]> Batches { get; } = [];
        public Task<IReadOnlyDictionary<int, IReadOnlyList<ArtworkResolution>>> ResolveAsync(IReadOnlyCollection<int> titleIds, CancellationToken ct = default)
        {
            Batches.Add(titleIds.ToArray());
            IReadOnlyDictionary<int, IReadOnlyList<ArtworkResolution>> result = titleIds.ToDictionary(id => id,
                id => (IReadOnlyList<ArtworkResolution>)Enum.GetValues<ArtworkRole>()
                    .Select(role => ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(id, role), [], [])).ToArray());
            return Task.FromResult(result);
        }
        public Task<ArtworkFileReference?> FindVariantAsync(int assetId, string name, string contentVersion, CancellationToken ct = default) => Task.FromResult<ArtworkFileReference?>(null);
    }
}
