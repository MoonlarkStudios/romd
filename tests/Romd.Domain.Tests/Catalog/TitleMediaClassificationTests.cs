using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleMediaClassificationTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ReclassifyMedia_PreferredScan_DisplacesOnlyTheSameTypeAndRelease()
    {
        var usa = Media(1, MediaType.BoxFront, "user", new(10, "USA", null, true));
        var europe = Media(2, MediaType.BoxFront, "user", new(11, "Europe", null, true));
        var usaBack = Media(3, MediaType.BoxBack, "user", new(10, "USA", null, true));
        var candidate = Media(4, MediaType.BoxFront, "user", new(10, "USA", null, false));
        var title = Title(usa, europe, usaBack, candidate);

        title.ReclassifyMedia(4, MediaType.BoxFront, new(10, "USA", "English", true))
            .ShouldBe(MediaReclassification.Changed);

        title.GetMediaById(4)!.IsPreferred.ShouldBeTrue();
        title.GetMediaById(4)!.Language.ShouldBe("English");
        title.GetMediaById(1)!.IsPreferred.ShouldBeFalse();
        title.GetMediaById(2)!.IsPreferred.ShouldBeTrue();
        title.GetMediaById(3)!.IsPreferred.ShouldBeTrue();
    }

    [Fact]
    public void ReclassifyMedia_TitleLevelPreference_IsUniquePerType()
    {
        var first = Media(1, MediaType.DiscLabel, "user", new(null, null, null, true));
        var second = Media(2, MediaType.DiscLabel, "gallery:screenscraper", MediaPlacement.TitleLevel);
        var title = Title(first, second);

        title.ReclassifyMedia(2, MediaType.DiscLabel, new(null, null, null, true));

        title.Media.Where(media => media.IsPreferred).Select(media => media.Id).ShouldBe([2]);
    }

    [Fact]
    public void ReclassifyMedia_CuratedCoverToBoxFront_MovesPrimaryAndKeepsTheOldTypeFilled()
    {
        var uploaded = Media(1, MediaType.Cover, "user", MediaPlacement.TitleLevel, isPrimary: true);
        var provider = Media(2, MediaType.Cover, "igdb", MediaPlacement.TitleLevel, createdAt: Epoch.AddDays(1));
        var title = Title(uploaded, provider);

        title.ReclassifyMedia(1, MediaType.BoxFront, new(10, "Japan", null, false))
            .ShouldBe(MediaReclassification.Changed);

        title.GetMediaById(1)!.Type.ShouldBe(MediaType.BoxFront);
        title.GetMediaById(1)!.CatalogReleaseId.ShouldBe(10);
        title.GetPrimaryMedia(MediaType.BoxFront)!.Id.ShouldBe(1);
        title.GetPrimaryMedia(MediaType.Cover)!.Id.ShouldBe(2);
    }

    [Theory]
    [InlineData("igdb", MediaType.Cover, MediaType.BoxFront)]
    [InlineData("user", MediaType.Manual, MediaType.BoxFront)]
    [InlineData("gallery:igdb", MediaType.Cover, MediaType.Manual)]
    public void ReclassifyMedia_LockedTypeChange_IsRejectedWithoutChangingTheItem(string source, MediaType from,
        MediaType to)
    {
        var media = Media(1, from, source, MediaPlacement.TitleLevel);
        var title = Title(media);

        title.ReclassifyMedia(1, to, MediaPlacement.TitleLevel).ShouldBe(MediaReclassification.TypeLocked);

        title.GetMediaById(1)!.Type.ShouldBe(from);
    }

    [Fact]
    public void ReclassifyMedia_EnrichmentMedia_CanStillBePlacedOnARelease()
    {
        var title = Title(Media(1, MediaType.Cover, "igdb", MediaPlacement.TitleLevel));

        title.ReclassifyMedia(1, MediaType.Cover, new(10, "USA", "English", false))
            .ShouldBe(MediaReclassification.Changed);

        title.GetMediaById(1)!.Placement.ShouldBe(new MediaPlacement(10, "USA", "English", false));
    }

    [Theory]
    [InlineData(MediaType.Screenshot)]
    [InlineData(MediaType.Cover)]
    [InlineData(MediaType.Manual)]
    public void ReclassifyMedia_PreferenceOutsidePackaging_IsRejected(MediaType type)
    {
        var title = Title(Media(1, type, "user", MediaPlacement.TitleLevel));

        title.ReclassifyMedia(1, type, new(null, null, null, true))
            .ShouldBe(MediaReclassification.PreferenceNotSupported);
        title.GetMediaById(1)!.IsPreferred.ShouldBeFalse();
    }

    [Fact]
    public void ReclassifyMedia_UnknownMedia_IsNotFound() =>
        Title().ReclassifyMedia(9, MediaType.BoxFront, MediaPlacement.TitleLevel).ShouldBe(MediaReclassification.NotFound);

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(null, "bad\nregion", null)]
    [InlineData(null, null, "\u0000")]
    public void MediaPlacement_InvalidInput_IsRejected(int? releaseId, string? region, string? language) =>
        MediaPlacement.TryCreate(releaseId, region, language, false).ShouldBeNull();

    [Fact]
    public void MediaPlacement_Text_IsTrimmedBlankIsAbsentAndLengthIsBounded()
    {
        MediaPlacement.TryCreate(4, "  Europe ", "   ", false).ShouldBe(new MediaPlacement(4, "Europe", null, false));
        MediaPlacement.TryCreate(null, new string('a', MediaPlacement.MaxTextLength), null, false).ShouldNotBeNull();
        MediaPlacement.TryCreate(null, new string('a', MediaPlacement.MaxTextLength + 1), null, false).ShouldBeNull();
    }

    [Fact]
    public void CreateNew_PreferredNonPackaging_Throws() =>
        Should.Throw<ArgumentException>(() => TitleMedia.CreateNew(1, MediaType.Screenshot, 1, "user", "image/png",
            Epoch, placement: new(null, null, null, true)));

    [Fact]
    public void Absorb_CuratedCollection_KeepsEveryDistinctFileWithItsFactsButNoPreference()
    {
        var target = Title();
        var source = Title(2,
            Media(5, MediaType.BoxFront, "gallery:screenscraper", new(10, "USA", null, true), titleId: 2,
                attribution: "ScreenScraper", providerAssetId: "box-2D-us"),
            Media(6, MediaType.BoxFront, "gallery:screenscraper", new(11, "Europe", null, false), titleId: 2,
                fileId: 700),
            Media(7, MediaType.Manual, "user", new(10, "USA", "English", false), titleId: 2, contentType: "application/pdf"));

        target.Absorb(source, ["igdb"], [], new(Epoch, []));

        target.Media.Count.ShouldBe(3);
        var usa = target.Media.Single(media => media.CatalogReleaseId == 10 && media.Type == MediaType.BoxFront);
        usa.Attribution.ShouldBe("ScreenScraper");
        usa.ProviderAssetId.ShouldBe("box-2D-us");
        usa.Region.ShouldBe("USA");
        usa.IsPreferred.ShouldBeFalse();
        target.Media.Single(media => media.Type == MediaType.Manual).ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public void ConsumerVisibility_ExcludesPackagingAndDocuments()
    {
        MediaTypes.ConsumerVisibleNames.ShouldBe(
            ["Cover", "Screenshot", "Banner", "Logo", "Background", "Video", "Box3d", "TitleScreen"]);
        Enum.GetValues<MediaType>().Where(MediaTypes.IsPackaging).Count().ShouldBe(7);
        MediaTypes.IsDocument(MediaType.Manual).ShouldBeTrue();
    }

    private static TitleMedia Media(int id, MediaType type, string source, MediaPlacement placement,
        bool isPrimary = false, DateTimeOffset? createdAt = null, int titleId = 1, string? attribution = null,
        string? providerAssetId = null, int? fileId = null, string contentType = "image/png") =>
        TitleMedia.Rehydrate(id, titleId, type, fileId ?? id * 100, source, contentType, isPrimary, null,
            createdAt ?? Epoch, attribution, null, placement, providerAssetId);

    private static Title Title(params TitleMedia[] media) => Title(1, media);

    private static Title Title(int id, params TitleMedia[] media) => Romd.Domain.Catalog.Title.Rehydrate(
        id: id, platformId: 10, name: "Test Title", normalizedName: "test title",
        description: null, publisher: null, developer: null, genre: null,
        releaseDate: null, players: null, rating: null,
        enrichmentStatus: EnrichmentStatus.None, lastEnrichedAt: null,
        createdAt: Epoch, media: media);
}
