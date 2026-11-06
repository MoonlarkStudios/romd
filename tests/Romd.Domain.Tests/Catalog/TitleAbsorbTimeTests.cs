using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleAbsorbTimeTests
{
    private static readonly DateTimeOffset Original = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Published = new DateTimeOffset(2026, 11, 4, 12, 13, 14, TimeSpan.FromHours(-3)).AddTicks(1234567);

    private static Title Title(int id, IEnumerable<TitleMetadataLayer>? layers = null,
        IEnumerable<TitleMedia>? media = null, IEnumerable<TitleExternalId>? links = null) =>
        Romd.Domain.Catalog.Title.Rehydrate(id, 10, "Title", "title", null, null, null, null, null, null, null,
            EnrichmentStatus.None, null, Original, metadataLayers: layers, media: media, externalIds: links);

    [Fact]
    public void Absorb_UnknownProviderFallback_UsesNewCopyRecencyInsteadOfOriginalSourceDates()
    {
        var first = TitleMetadataLayer.Rehydrate(1, 2, "first", MetadataSourceType.Provider,
            """{"publisher":"First"}""", Original.AddYears(10));
        var second = TitleMetadataLayer.Rehydrate(2, 2, "second", MetadataSourceType.Provider,
            """{"publisher":"Second"}""", Original);
        var target = Title(1);

        target.Absorb(Title(2, [first, second]), [], [], new(Published,
            new Dictionary<string, DateTimeOffset> { ["first"] = Published, ["second"] = Published.AddSeconds(1) }));

        var copied = target.MetadataLayers.ToArray();
        copied[0].UpdatedAt.ShouldBe(Published);
        copied[1].UpdatedAt.ShouldBe(Published.AddSeconds(1));
        target.Publisher.ShouldBe("Second");
        target.FieldProvenance[nameof(Romd.Domain.Catalog.Title.Publisher)].ShouldBe("second");
    }

    [Fact]
    public void Absorb_UnknownMediaSourcesAndNewLinks_UsesCopyDatesAndKeepsFirstMediaAsPrimary()
    {
        var first = TitleMedia.Rehydrate(1, 2, MediaType.Cover, 11, "first", "image/png", false, null, Original.AddYears(10));
        var second = TitleMedia.Rehydrate(2, 2, MediaType.Cover, 12, "second", "image/png", false, null, Original);
        var link = TitleExternalId.Rehydrate(1, 2, "provider", "42", 1, false, Original);
        var target = Title(1);

        target.Absorb(Title(2, media: [first, second], links: [link]), [], [], new(Published, []));

        target.Media.ShouldAllBe(value => value.CreatedAt == Published && value.CreatedAt.Offset == Published.Offset);
        target.ExternalIds.ShouldHaveSingleItem().CreatedAt.ShouldBe(Published);
        target.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull().SourceId.ShouldBe("first");
    }

    [Fact]
    public void Absorb_DuplicateSourceIds_UsesFirstImportedLayerAndFirstCaseInsensitiveTimestamp()
    {
        var first = TitleMetadataLayer.Rehydrate(1, 2, "Provider", MetadataSourceType.Provider,
            """{"publisher":"First"}""", Original);
        var duplicate = TitleMetadataLayer.Rehydrate(2, 2, "provider", MetadataSourceType.Provider,
            """{"publisher":"Duplicate"}""", Original.AddYears(10));
        var target = Title(1);
        var times = new TitleAbsorptionTimes(Published,
            [new("PROVIDER", Published), new("provider", Published.AddSeconds(1))]);

        target.Absorb(Title(2, [first, duplicate]), [], [], times);

        var imported = target.MetadataLayers.ShouldHaveSingleItem();
        imported.UpdatedAt.ShouldBe(Published);
        imported.GetPayload().ShouldNotBeNull().Publisher.ShouldBe("First");
        target.Publisher.ShouldBe("First");
    }

    [Fact]
    public void Absorb_MissingRequiredLayerTimestamp_RejectsBeforeChangingAnyTargetState()
    {
        var target = Title(1);
        target.StoreUserLayer(new() { Description = "Existing" }, Original);
        target.Rematerialize([]);
        var view = target.MetadataLayers;
        var layer = TitleMetadataLayer.Rehydrate(1, 2, "provider", MetadataSourceType.Provider, "{}", Original);
        var media = TitleMedia.Rehydrate(1, 2, MediaType.Cover, 11, "provider", "image/png", false, null, Original);
        var link = TitleExternalId.Rehydrate(1, 2, "provider", "42", 1, false, Original);
        var revision = target.Revision;

        Should.Throw<ArgumentException>(() => target.Absorb(Title(2, [layer], [media], [link]), [], [], new(Published, [])));

        target.Description.ShouldBe("Existing");
        target.FieldProvenance[nameof(Romd.Domain.Catalog.Title.Description)].ShouldBe("user");
        target.MetadataLayers.ShouldBeSameAs(view);
        target.MetadataLayers.ShouldHaveSingleItem().SourceId.ShouldBe("user");
        target.ExternalIds.ShouldBeEmpty();
        target.Media.ShouldBeEmpty();
        target.ContentRatingsMaterialized.ShouldBeTrue();
        target.Revision.ShouldBe(revision);
    }

    [Fact]
    public void AbsorptionTimes_CallerMutatesTimestampMap_OwnsReadOnlyCaseInsensitiveFacts()
    {
        var source = new Dictionary<string, DateTimeOffset> { ["Provider"] = Published };
        var times = new TitleAbsorptionTimes(Published, source);

        source["Provider"] = Original;

        times.MetadataLayerUpdatedAt["PROVIDER"].ShouldBe(Published);
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, DateTimeOffset>)times.MetadataLayerUpdatedAt).Clear());
    }
}
