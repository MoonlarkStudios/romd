using System.Reflection;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Collections;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleEncapsulationTests
{
    [Fact]
    public void Collections_ThroughMutableInterfaces_RejectExternalChanges()
    {
        var title = Title.CreateNew(1, "Example", "example", createdAt: DateTimeOffset.UnixEpoch);

        Should.Throw<NotSupportedException>(() => ((ICollection<TitleExternalId>)title.ExternalIds)
            .Add(TitleExternalId.CreateNew(title.Id, "igdb", "1", 1, createdAt: DateTimeOffset.UnixEpoch)));
        Should.Throw<NotSupportedException>(() => ((ICollection<TitleMedia>)title.Media)
            .Add(TitleMedia.CreateNew(title.Id, MediaType.Cover, 1, "igdb", "image/png", createdAt: DateTimeOffset.UnixEpoch)));
        Should.Throw<NotSupportedException>(() => ((ICollection<TitleMetadataLayer>)title.MetadataLayers)
            .Add(TitleMetadataLayer.CreateNew(title.Id, "igdb", MetadataSourceType.Provider, new TitleMetadataPayload(), updatedAt: DateTimeOffset.UnixEpoch)));
        Should.Throw<NotSupportedException>(() => ((ICollection<ContentRating>)title.ContentRatings).Clear());

        title.ExternalIds.ShouldBeEmpty();
        title.Media.ShouldBeEmpty();
        title.MetadataLayers.ShouldBeEmpty();
        title.ContentRatings.ShouldBeEmpty();
        title.ContentRatingsMaterialized.ShouldBeFalse();
    }

    [Fact]
    public void CollectionViews_AfterAggregateChanges_StayCurrentWithoutNewWrappers()
    {
        var title = Title.CreateNew(1, "Example", "example", createdAt: DateTimeOffset.UnixEpoch);
        var mediaView = title.Media;
        var layersView = title.MetadataLayers;
        var ratingsView = title.ContentRatings;
        var media = TitleMedia.CreateNew(title.Id, MediaType.Cover, 1, "igdb", "image/png", createdAt: DateTimeOffset.UnixEpoch);

        title.AddMedia(media);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E10" }]
        }, updatedAt: DateTimeOffset.UnixEpoch);
        title.Rematerialize(["igdb"]);

        title.Media.ShouldBeSameAs(mediaView);
        title.MetadataLayers.ShouldBeSameAs(layersView);
        title.ContentRatings.ShouldBeSameAs(ratingsView);
        var retainedMedia = mediaView.ShouldHaveSingleItem();
        retainedMedia.ShouldNotBeSameAs(media);
        retainedMedia.FileId.ShouldBe(media.FileId);
        layersView.ShouldHaveSingleItem().SourceId.ShouldBe("igdb");
        ratingsView.ShouldHaveSingleItem().Code.ShouldBe("E10+");
    }

    [Fact]
    public void MetadataDictionaries_ThroughMutableInterfaces_RejectChangesAndKeepLiveViews()
    {
        var title = Title.CreateNew(1, "Example", "example", createdAt: DateTimeOffset.UnixEpoch);
        var overridesView = title.FieldSourceOverrides;
        var provenanceView = title.FieldProvenance;
        title.SetFieldSourceOverride(nameof(Title.Description), "igdb");
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "Provider description" }, updatedAt: DateTimeOffset.UnixEpoch);
        title.Rematerialize(["igdb"]);

        Should.Throw<NotSupportedException>(() => ((IDictionary<string, string>)overridesView)
            [nameof(Title.Description)] = "user");
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, string>)provenanceView).Clear());
        title.FieldSourceOverrides.ShouldBeSameAs(overridesView);
        title.FieldProvenance.ShouldBeSameAs(provenanceView);
        overridesView[nameof(Title.Description)].ShouldBe("igdb");
        provenanceView[nameof(Title.Description)].ShouldBe("igdb");

        title.ClearFieldSourceOverride(nameof(Title.Description));
        overridesView.ShouldBeEmpty();
    }

    [Fact]
    public void SetPrimaryMedia_TransientMediaSharingZeroId_SelectsOnlyOneInstance()
    {
        var title = Title.CreateNew(1, "Example", "example", createdAt: DateTimeOffset.UnixEpoch);
        var first = TitleMedia.CreateNew(title.Id, MediaType.Cover, 1, "igdb", "image/png", createdAt: DateTimeOffset.UnixEpoch);
        var second = TitleMedia.CreateNew(title.Id, MediaType.Cover, 2, "user", "image/png", createdAt: DateTimeOffset.UnixEpoch);
        title.AddMedia(first);
        title.AddMedia(second);

        title.SetPrimaryMedia(first.Id);

        title.Media.Count(media => media.IsPrimary).ShouldBe(1);
        title.GetPrimaryMedia(MediaType.Cover).ShouldBeSameAs(title.Media.First());
        first.IsPrimary.ShouldBeFalse();

        title.RecalculatePrimaryMedia(["igdb"]);
        title.Media.Count(media => media.IsPrimary).ShouldBe(1);
        title.GetPrimaryMedia(MediaType.Cover).ShouldBeSameAs(title.Media.Last());
        second.IsPrimary.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMedia_SharedInput_GivesEachAggregateAnIndependentChild(bool replace)
    {
        var first = Title.CreateNew(1, "First", "first", DateTimeOffset.UnixEpoch);
        var second = Title.CreateNew(1, "Second", "second", DateTimeOffset.UnixEpoch);
        var input = TitleMedia.CreateNew(first.Id, MediaType.Cover, 17, "igdb", "image/png", DateTimeOffset.UnixEpoch,
            "https://example.test/cover.png", "Artist", "https://example.test/credits");
        if (replace)
        {
            first.AddOrReplaceMedia(input);
            second.AddOrReplaceMedia(input);
        }
        else
        {
            first.AddMedia(input);
            second.AddMedia(input);
        }

        first.SetPrimaryMedia(input.Id);

        first.Media.ShouldHaveSingleItem().IsPrimary.ShouldBeTrue();
        second.Media.ShouldHaveSingleItem().IsPrimary.ShouldBeFalse();
        input.IsPrimary.ShouldBeFalse();
        first.Media.Single().ShouldNotBeSameAs(second.Media.Single());
        foreach (var retained in first.Media.Concat(second.Media))
        {
            retained.Attribution.ShouldBe("Artist");
            retained.SourcePageUrl.ShouldBe("https://example.test/credits");
            retained.SourceUrl.ShouldBe("https://example.test/cover.png");
            retained.FileId.ShouldBe(17);
            retained.CreatedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        }
    }

    [Fact]
    public void Rehydrate_SharedInputAndMutableCollection_CapturesIndependentChildrenAndAllFacts()
    {
        var input = TitleMedia.Rehydrate(11, 7, MediaType.Cover, 17, "igdb", "image/png", false,
            "https://example.test/cover.png", DateTimeOffset.UnixEpoch, "Artist", "https://example.test/credits");
        var source = new List<TitleMedia> { input };
        var first = Restore();
        var second = Restore();
        source.Clear();

        first.SetPrimaryMedia(11);

        first.Media.ShouldHaveSingleItem().IsPrimary.ShouldBeTrue();
        second.Media.ShouldHaveSingleItem().IsPrimary.ShouldBeFalse();
        input.IsPrimary.ShouldBeFalse();
        first.Media.Single().Id.ShouldBe(11);
        first.Media.Single().TitleId.ShouldBe(7);
        first.Media.Single().Attribution.ShouldBe("Artist");
        first.Media.Single().SourcePageUrl.ShouldBe("https://example.test/credits");

        Title Restore() => Title.Rehydrate(7, 1, "Example", "example", null, null, null, null, null, null, null,
            EnrichmentStatus.None, null, DateTimeOffset.UnixEpoch, media: source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMedia_ForeignTitleIdentity_RejectsBeforeChangingTheAggregate(bool replace)
    {
        var title = Title.CreateNew(1, "Example", "example", DateTimeOffset.UnixEpoch);
        title.AddMedia(TitleMedia.CreateNew(title.Id, MediaType.Cover, 17, "igdb", "image/png", DateTimeOffset.UnixEpoch));
        title.SetPrimaryMedia(0);
        var original = title.Media.Single();
        var foreign = TitleMedia.CreateNew(7, MediaType.Cover, 19, "igdb", "image/png", DateTimeOffset.UnixEpoch);

        Should.Throw<ArgumentException>(() =>
        {
            if (replace)
                title.AddOrReplaceMedia(foreign);
            else
                title.AddMedia(foreign);
        });

        title.Media.ShouldHaveSingleItem().ShouldBeSameAs(original);
        original.IsPrimary.ShouldBeTrue();
        original.FileId.ShouldBe(17);
    }

    [Fact]
    public void ChildMutators_ThatBypassAggregateRules_AreNotPublic()
    {
        const BindingFlags publicMethods = BindingFlags.Instance | BindingFlags.Public;

        typeof(TitleMedia).GetMethod("SetPrimary", publicMethods).ShouldBeNull();
        typeof(TitleMedia).GetMethod("SetAttribution", publicMethods).ShouldBeNull();
        typeof(TitleMetadataLayer).GetMethod("UpdateData", publicMethods).ShouldBeNull();
        typeof(CollectionItem).GetMethod("UpdateSortOrder", publicMethods).ShouldBeNull();
    }
}
