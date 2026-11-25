using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed partial class ProviderMatchPersistenceTests
{
    [Theory]
    [InlineData("replace")]
    [InlineData("unlink")]
    [InlineData("confirm")]
    [InlineData("same game")]
    public async Task ChangeMatch_ConfirmedLink_WithdrawsOnlyItsAutomaticContributionsOnRemoval(string action)
    {
        await Seed();
        await using var db = _database.CreateContext();
        await SeedConfirmedContributionsAsync(db);
        _adapter.Capabilities.Returns(["metadata", "artwork"]);
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var match = (await service.GetAsync(1, "test", default)).Value;
        var precondition = RevisionPrecondition.For(match.Revision.ToString());
        var staleSnapshot = (await new TitleRepository(db).GetAggregateAsync(1)).ShouldNotBeNull();

        var changed = action switch
        {
            "replace" => await service.SetAsync(1, "test", "43", precondition, default),
            "unlink" => await service.UnlinkAsync(1, "test", precondition, default),
            "same game" => await service.SetAsync(1, "test", "42", precondition, default),
            _ => await service.ConfirmAsync(1, "test", precondition, default)
        };

        changed.IsError.ShouldBeFalse();
        changed.Value.ExternalId.ShouldBe(action == "unlink" ? null : action == "replace" ? "43" : "42");
        changed.Value.PendingJobId.HasValue.ShouldBe(action != "unlink");
        changed.Value.MetadataNeedsRefresh.ShouldBe(action == "replace");
        await using var observer = _database.CreateContext();
        var title = (await new TitleRepository(observer).GetAggregateAsync(1)).ShouldNotBeNull();
        bool withdrawn = action is "replace" or "unlink";
        title.Name.ShouldBe("Game");
        title.Description.ShouldBe(withdrawn ? "Fallback description" : "Old description");
        title.Publisher.ShouldBe(withdrawn ? null : "Old publisher");
        title.Developer.ShouldBe("Curator's developer");
        title.Genre.ShouldBe(withdrawn ? "Strategy" : "Action");
        title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe(withdrawn ? "other" : "test");
        title.ConservativeMinimumAge.ShouldBe(withdrawn ? 12 : 18);
        title.FieldProvenance["Description"].ShouldBe(withdrawn ? "other" : "test");
        title.FieldSourceOverrides["Description"].ShouldBe("test");
        title.MetadataLayers.Any(layer => layer.SourceId == "test").ShouldBe(!withdrawn);
        title.GetMediaByTypeAndSource(MediaType.Cover, "gallery:test").ShouldNotBeNull();
        title.GetMediaByTypeAndSource(MediaType.Cover, "other").ShouldNotBeNull().IsPrimary.ShouldBe(withdrawn);
        title.Media.Any(media => media.SourceId == "test").ShouldBe(!withdrawn);
        (await observer.Libraries.SingleAsync()).NeedsMaterialization.ShouldBe(withdrawn);
        (await observer.Set<ArtworkAcquisitionEntity>().AnyAsync()).ShouldBe(!withdrawn);

        var reader = new ArtworkReader(observer);
        var resolved = (await reader.ResolveAsync([1]))[1];
        resolved.Single(role => role.Role == ArtworkRole.Poster).Asset.ShouldNotBeNull().Id.ShouldBe(withdrawn ? 4 : 1);
        resolved.Single(role => role.Role == ArtworkRole.Hero).Asset.ShouldNotBeNull().Id.ShouldBe(2);
        (await observer.ArtworkAssets.AnyAsync(asset => asset.Id == 3 && asset.IsRejected)).ShouldBeTrue();
        (await reader.FindVariantAsync(1, "large", "v1") is null).ShouldBe(withdrawn);
        (await reader.FindVariantAsync(2, "large", "v1")).ShouldNotBeNull();
        (await observer.Files.CountAsync()).ShouldBe(1);
        if (withdrawn)
        {
            // An enrichment worker that loaded the old association cannot put its layers back.
            await using var transaction = await observer.Database.BeginTransactionAsync();
            await Should.ThrowAsync<PersistenceConflictException>(() =>
                new TitleRepository(observer).ApplyEnrichmentStagedAsync(staleSnapshot));
        }
    }

    [Fact]
    public async Task Replace_RematerializationFails_RollsBackLinkAndContributions()
    {
        await Seed();
        await using var db = _database.CreateContext();
        await SeedConfirmedContributionsAsync(db);
        var rematerializer = Substitute.For<IMetadataRematerializer>();
        rematerializer.RematerializeAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Rematerialization failed.")));
        var service = ProviderMatchTestServices.Create(db, [_adapter], rematerializer: rematerializer);
        var match = (await service.GetAsync(1, "test", default)).Value;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.SetAsync(1, "test", "43", RevisionPrecondition.For(match.Revision.ToString()), default));

        await using var observer = _database.CreateContext();
        (await observer.TitleExternalIds.SingleAsync()).ExternalId.ShouldBe("42");
        (await observer.TitleMetadataLayers.AnyAsync(layer => layer.SourceId == "test")).ShouldBeTrue();
        (await observer.TitleMedia.AnyAsync(media => media.SourceId == "test")).ShouldBeTrue();
        (await observer.ArtworkAssets.AnyAsync(asset => asset.Id == 1)).ShouldBeTrue();
        (await observer.Titles.SingleAsync()).Description.ShouldBe("Old description");
        (await observer.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeFalse();
        (await observer.Jobs.CountAsync()).ShouldBe(0);
    }

    private static async Task SeedConfirmedContributionsAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UnixEpoch;
        db.TitleExternalIds.Add(new TitleExternalIdEntity { TitleId = 1, Provider = "test", ExternalId = "42", IsConfirmed = true });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromBytes(new byte[Sha256.ByteLength]), Size = 1, SizeOnDisk = 1 });
        db.Libraries.Add(new LibraryEntity { Id = 1, Name = "All titles", ConfigurationJson = "{}", NeedsMaterialization = false });
        db.TitleMedia.AddRange(
            new TitleMediaEntity { TitleId = 1, Type = "Cover", FileId = 1, SourceId = "test" },
            new TitleMediaEntity { TitleId = 1, Type = "Cover", FileId = 1, SourceId = "other", CreatedAt = now.AddDays(1) },
            new TitleMediaEntity { TitleId = 1, Type = "Cover", FileId = 1, SourceId = "gallery:test", CreatedAt = now.AddDays(2) });
        db.ArtworkPreferences.Add(new ArtworkPreferenceEntity { Role = ArtworkRole.Poster, SourceId = "test", Priority = 0 });
        db.ArtworkAssets.AddRange(Asset(1, ArtworkRole.Poster), Asset(2, ArtworkRole.Hero),
            Asset(3, ArtworkRole.Backdrop, rejected: true), Asset(4, ArtworkRole.Poster, "other"));
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 1,
            Role = ArtworkRole.Hero,
            Mode = ArtworkSelectionMode.Pinned,
            PinnedAssetId = 2
        });
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = ArtworkRole.Poster, Status = "Updated", SourceId = "test" });
        db.TitleMetadataLayers.AddRange(
            Layer("user", MetadataSourceType.User, new() { Developer = "Curator's developer" }),
            Layer("test", MetadataSourceType.Provider, new()
            {
                ProviderGameId = "42",
                Description = "Old description",
                Publisher = "Old publisher",
                Genre = "Action",
                ContentRatings = [new() { Board = RatingBoard.Pegi, RawCode = "18" }]
            }),
            Layer("other", MetadataSourceType.Provider, new()
            {
                ProviderGameId = "9",
                Description = "Fallback description",
                Genre = "Strategy",
                ContentRatings = [new() { Board = RatingBoard.Pegi, RawCode = "12" }]
            }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var titles = new TitleRepository(db);
            var title = (await titles.GetAggregateAsync(1)).ShouldNotBeNull();
            title.SetFieldSourceOverride("Description", "test");
            title.Rematerialize(["test", "other"]);
            title.RecalculatePrimaryMedia(["test", "other", "gallery:test"]);
            await titles.UpdateMaterializedMetadataStagedAsync(title);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();

        TitleMetadataLayerEntity Layer(string sourceId, MetadataSourceType sourceType, TitleMetadataPayload payload) =>
            TitleMetadataLayerEntity.FromDomain(TitleMetadataLayer.CreateNew(1, sourceId, sourceType, payload, now));

        static ArtworkAssetEntity Asset(int id, ArtworkRole role, string sourceId = "test", bool rejected = false) =>
            new()
            {
                Id = id,
                TitleId = 1,
                Role = role,
                SourceId = sourceId,
                ProviderGameId = sourceId == "test" ? "42" : "9",
                ProviderAssetId = id.ToString(),
                OriginalFileId = 1,
                ContentVersion = "v1",
                ContentType = "image/webp",
                Width = 600,
                Height = 900,
                IsEligible = true,
                IsRejected = rejected,
                Variants = [new() { Name = "large", FileId = 1, ContentVersion = "v1", ContentType = "image/webp", Width = 600, Height = 900 }]
            };
    }
}
