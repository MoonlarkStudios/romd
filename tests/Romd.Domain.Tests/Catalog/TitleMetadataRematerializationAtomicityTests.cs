using System.Collections;
using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleMetadataRematerializationAtomicityTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Revision = new("b0da15c2-722a-4633-a2f8-3d63b30c93a0");

    private static Title Create(bool ratingsMaterialized, string json) => Title.Rehydrate(
        1, 10, "DAT Name", "dat name", "Existing description", "Existing publisher", "Existing developer", "Existing genre",
        new(1994, 1, 1), 2, 95, EnrichmentStatus.Completed, Timestamp, Timestamp,
        metadataLayers: [TitleMetadataLayer.Rehydrate(1, 1, "provider", MetadataSourceType.Provider, json, Timestamp)],
        fieldProvenance: new() { [nameof(Title.Description)] = "existing" },
        contentRatings: ratingsMaterialized ?
        [new() { Board = RatingBoard.Esrb, Code = "T", Designation = RatingDesignation.Rated, MinimumAge = 13, SourceId = "existing" }] : null,
        conservativeMinimumAge: ratingsMaterialized ? 13 : null, revision: Revision);

    private static void AssertUnchanged(Title title, bool ratingsMaterialized)
    {
        title.Name.ShouldBe("DAT Name");
        title.NormalizedName.ShouldBe("dat name");
        title.Description.ShouldBe("Existing description");
        title.Publisher.ShouldBe("Existing publisher");
        title.Developer.ShouldBe("Existing developer");
        title.Genre.ShouldBe("Existing genre");
        title.ReleaseDate.ShouldBe(new DateOnly(1994, 1, 1));
        title.Players.ShouldBe(2);
        title.Rating.ShouldBe(95);
        title.FieldProvenance.ShouldHaveSingleItem().ShouldBe(new(nameof(Title.Description), "existing"));
        title.ContentRatingsMaterialized.ShouldBe(ratingsMaterialized);
        title.ConservativeMinimumAge.ShouldBe(ratingsMaterialized ? 13 : null);
        if (ratingsMaterialized)
            title.ContentRatings.ShouldHaveSingleItem().SourceId.ShouldBe("existing");
        else
            title.ContentRatings.ShouldBeEmpty();
        title.Revision.ShouldBe(Revision);
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Completed);
        title.LastEnrichedAt.ShouldBe(Timestamp);
        title.CreatedAt.ShouldBe(Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rematerialize_InvalidRetainedRating_DoesNotPartiallyApplyFieldsOrResetRatings(bool ratingsMaterialized)
    {
        var title = Create(ratingsMaterialized, """{"description":"Replacement","contentRatings":[null]}""");

        Should.Throw<ArgumentNullException>(() => title.Rematerialize(["provider"]));

        AssertUnchanged(title, ratingsMaterialized);
    }

    [Fact]
    public void Rematerialize_InvalidRetainedJson_LeavesExistingEffectiveStateUntouched()
    {
        var title = Create(true, "{");

        Should.Throw<JsonException>(() => title.Rematerialize(["provider"]));

        AssertUnchanged(title, true);
    }

    [Fact]
    public void Rematerialize_FieldSourceLookupFails_LeavesExistingEffectiveStateUntouched()
    {
        var title = Create(true, """{"description":"Replacement","publisher":"Replacement"}""");

        Should.Throw<InvalidOperationException>(() => title.Rematerialize(["provider"], new FailingDefaults()));

        AssertUnchanged(title, true);
    }

    [Fact]
    public void Rematerialize_IgnoredAdditionalUserLayerHasInvalidJson_PreservesFirstUserBehavior()
    {
        var title = Title.Rehydrate(1, 10, "DAT Name", "dat name", null, null, null, null, null, null, null,
            EnrichmentStatus.None, null, Timestamp, metadataLayers:
            [
                TitleMetadataLayer.Rehydrate(1, 1, "USER", MetadataSourceType.User, """{"description":"First user"}""", Timestamp),
                TitleMetadataLayer.Rehydrate(2, 1, "user", MetadataSourceType.User, "{", Timestamp)
            ]);

        title.Rematerialize([]);

        title.Description.ShouldBe("First user");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("user");
    }

    private sealed class FailingDefaults : IReadOnlyDictionary<string, string>
    {
        public bool TryGetValue(string key, out string value)
        {
            if (key == nameof(Title.Rating))
                throw new InvalidOperationException("Cannot read configured rating source.");
            value = "provider";
            return true;
        }

        public string this[string key] => throw new NotSupportedException();
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public int Count => 0;
        public bool ContainsKey(string key) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
