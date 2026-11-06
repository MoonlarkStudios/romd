using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleMetadataMaterializationParityTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TitleMetadataLayer Layer(string source, string description, string code, DateTimeOffset? updatedAt = null) =>
        TitleMetadataLayer.Rehydrate(0, 1, source, MetadataSourceType.Provider,
            JsonSerializer.Serialize(new TitleMetadataPayload
            {
                Name = "Provider name",
                Description = description,
                ContentRatings = [new() { Board = RatingBoard.Esrb, RawCode = code }]
            }, MetadataJsonOptions.Default), updatedAt ?? Timestamp);

    private static Title Create(params TitleMetadataLayer[] layers) => Title.Rehydrate(
        1, 10, "DAT Name", "dat name", null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, Timestamp, metadataLayers: layers);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rematerialize_EqualPriorityAndTimestamp_PreservesInputOrder(bool reverse)
    {
        var first = Layer("first", "First", "E");
        var second = Layer("second", "Second", "M");
        var title = reverse ? Create(second, first) : Create(first, second);

        title.Rematerialize([]);

        title.Description.ShouldBe(reverse ? "Second" : "First");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe(reverse ? "second" : "first");
        title.ContentRatings.ShouldHaveSingleItem().Code.ShouldBe(reverse ? "M" : "E");
        title.Name.ShouldBe("DAT Name");
        title.NormalizedName.ShouldBe("dat name");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rematerialize_UnknownSourceRecency_GlobalPriorityStillTakesPrecedence(bool explicitPriority)
    {
        var title = Create(Layer("old", "Old", "E"), Layer("new", "New", "M", Timestamp.AddDays(1)));

        title.Rematerialize(explicitPriority ? ["OLD"] : []);

        title.Description.ShouldBe(explicitPriority ? "Old" : "New");
        title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe(explicitPriority ? "old" : "new");
    }

    [Fact]
    public void Rematerialize_OverrideSourceCasing_PreservesConfiguredProvenance()
    {
        var title = Create(Layer("igdb", "Provider", "M"), Layer("USER", "User", "E"));
        title.SetFieldSourceOverride(nameof(Title.Description), "IGDB");
        title.SetFieldSourceOverride(Title.ContentRatingsFieldName, "IgDb");

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBe("Provider");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("IGDB");
        title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe("IgDb");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rematerialize_FieldKeys_RespectEachDictionaryLookupSemantics(bool caseInsensitivePlatform)
    {
        var title = Create(Layer("first", "First", "E"), Layer("second", "Second", "M"));
        title.SetFieldSourceOverride("description", "second");
        var defaults = new Dictionary<string, string>(caseInsensitivePlatform ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["description"] = "SECOND",
            ["contentratings"] = "SECOND"
        };

        title.Rematerialize(["first", "second"], defaults);

        title.Description.ShouldBe(caseInsensitivePlatform ? "Second" : "First");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe(caseInsensitivePlatform ? "SECOND" : "first");
        title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe(caseInsensitivePlatform ? "SECOND" : "first");
    }

    [Fact]
    public void Rematerialize_DuplicateSourceIds_PreservesLastOrderedPayloadAndFirstSourceSpelling()
    {
        var title = Create(Layer("IGDB", "New", "M", Timestamp.AddDays(1)), Layer("igdb", "Old", "E"));

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBe("Old");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("IGDB");
        var rating = title.ContentRatings.ShouldHaveSingleItem();
        rating.Code.ShouldBe("E");
        rating.SourceId.ShouldBe("IGDB");
    }

    [Fact]
    public void Rematerialize_MultipleUserLayers_UsesFirstAndCanonicalUserProvenance()
    {
        var title = Create(Layer("USER", "First user", "E"), Layer("user", "Second user", "M", Timestamp.AddDays(1)));

        title.Rematerialize([]);

        title.Description.ShouldBe("First user");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("user");
        title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe("user");
    }
}
