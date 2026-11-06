using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleMetadataResolutionPolicyTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static TitleMetadataLayerFacts Layer(string source, TitleMetadataPayload payload) => new(source, Timestamp, payload);
    private static ContentRatingClaim Claim(RatingBoard board, string code) => new() { Board = board, RawCode = code };

    [Fact]
    public void Resolve_FullCascade_ResolvesCompletedFieldsProvenanceAndRatingsIndependently()
    {
        var input = new TitleMetadataResolutionInput(
        [
            Layer("user", new() { Publisher = "User publisher", ContentRatings = [Claim(RatingBoard.Esrb, "E")] }),
            Layer("primary", new()
            {
                Description = "Primary description", Genre = "", Players = 0, Rating = 0,
                ContentRatings = [Claim(RatingBoard.Esrb, "M"), Claim(RatingBoard.Pegi, "12")]
            }),
            Layer("secondary", new()
            {
                Description = "Override description", Developer = "Platform developer", ReleaseDate = new(1994, 1, 1),
                ContentRatings = [Claim(RatingBoard.Cero, "D")]
            })
        ], ["primary", "secondary"], new Dictionary<string, string> { [nameof(Title.Description)] = "SECONDARY" },
            new Dictionary<string, string> { [nameof(Title.Developer)] = "secondary" });

        var result = TitleMetadataResolutionPolicy.Resolve(input);

        result.Description.ShouldBe("Override description");
        result.Publisher.ShouldBe("User publisher");
        result.Developer.ShouldBe("Platform developer");
        result.Genre.ShouldBe("");
        result.ReleaseDate.ShouldBe(new DateOnly(1994, 1, 1));
        result.Players.ShouldBe(0);
        result.Rating.ShouldBe(0);
        result.FieldProvenance[nameof(Title.Description)].ShouldBe("SECONDARY");
        result.FieldProvenance[nameof(Title.Publisher)].ShouldBe("user");
        result.FieldProvenance[nameof(Title.Developer)].ShouldBe("secondary");
        result.FieldProvenance.ShouldNotContainKey(nameof(Title.Name));
        result.FieldProvenance.ShouldNotContainKey(Title.ContentRatingsFieldName);
        result.ContentRatings.Select(value => (value.Board, value.SourceId)).ShouldBe(
            [(RatingBoard.Esrb, "user"), (RatingBoard.Pegi, "primary"), (RatingBoard.Cero, "secondary")]);
        result.ConservativeMinimumAge.ShouldBe(17);
    }

    [Fact]
    public void Resolve_ExplicitDistinctPriorities_AllLayerPermutationsProduceSameResult()
    {
        var first = Layer("first", new() { Description = "Winner", ContentRatings = [Claim(RatingBoard.Esrb, "T")] });
        var second = Layer("second", new() { Publisher = "Publisher", ContentRatings = [Claim(RatingBoard.Pegi, "12")] });
        var third = Layer("third", new() { Description = "Other", ContentRatings = [Claim(RatingBoard.Esrb, "M")] });
        TitleMetadataLayerFacts[][] permutations =
        [
            [first, second, third], [first, third, second], [second, first, third],
            [second, third, first], [third, first, second], [third, second, first]
        ];

        foreach (var layers in permutations)
        {
            var result = TitleMetadataResolutionPolicy.Resolve(new(layers, ["first", "second", "third"], new Dictionary<string, string>()));

            result.Description.ShouldBe("Winner");
            result.Publisher.ShouldBe("Publisher");
            result.FieldProvenance[nameof(Title.Description)].ShouldBe("first");
            result.ContentRatings.Select(value => value.Code).ShouldBe(["T", "PEGI 12"]);
            result.ConservativeMinimumAge.ShouldBe(13);
        }
    }

    [Fact]
    public void Resolve_CallerMutatesInputCollections_KeepsCapturedScalarAndNestedRatingFacts()
    {
        string[] descriptors = ["Original descriptor"];
        ContentRatingClaim[] claims = [new() { Board = RatingBoard.Esrb, RawCode = "T", Descriptors = descriptors }];
        var first = Layer("first", new() { Description = "First", ContentRatings = claims });
        var second = Layer("second", new() { Description = "Second" });
        TitleMetadataLayerFacts[] layers = [first, second];
        string[] priorities = ["first", "second"];
        var overrides = new Dictionary<string, string> { [nameof(Title.Description)] = "first" };
        var defaults = new Dictionary<string, string> { [Title.ContentRatingsFieldName] = "first" };
        var input = new TitleMetadataResolutionInput(layers, priorities, overrides, defaults);

        descriptors[0] = "Changed descriptor";
        claims[0] = Claim(RatingBoard.Esrb, "M");
        layers[0] = second;
        priorities[0] = "second";
        overrides[nameof(Title.Description)] = "second";
        defaults[Title.ContentRatingsFieldName] = "second";
        var result = TitleMetadataResolutionPolicy.Resolve(input);

        result.Description.ShouldBe("First");
        var rating = result.ContentRatings.ShouldHaveSingleItem();
        rating.Code.ShouldBe("T");
        rating.Descriptors.ShouldBe(["Original descriptor"]);
        result.ConservativeMinimumAge.ShouldBe(13);
        input.GlobalSourcePriority.ShouldBe(["first", "second"]);
        input.PlatformDefaults[Title.ContentRatingsFieldName].ShouldBe("first");
    }

    [Fact]
    public void Collections_ThroughMutableInterfaces_RejectChangesToFactsAndCompletedResults()
    {
        var layer = Layer("first", new()
        {
            Description = "Original",
            ContentRatings =
            [new() { Board = RatingBoard.Esrb, RawCode = "T", Descriptors = ["Original descriptor"] }]
        });
        var input = new TitleMetadataResolutionInput([layer], ["first"], new Dictionary<string, string>());
        var result = TitleMetadataResolutionPolicy.Resolve(input);

        Should.Throw<NotSupportedException>(() => ((ICollection<ContentRatingClaim?>)layer.ContentRatings).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)layer.ContentRatings[0].ShouldNotBeNull().Descriptors)[0] = "Changed");
        Should.Throw<NotSupportedException>(() => ((ICollection<TitleMetadataLayerFacts>)input.Layers).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)input.GlobalSourcePriority)[0] = "other");
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, string>)input.FieldSourceOverrides).Add("Description", "other"));
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, string>)input.PlatformDefaults).Add("Description", "other"));
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, string>)result.FieldProvenance).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<ContentRating>)result.ContentRatings).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)result.ContentRatings[0].Descriptors)[0] = "Changed");

        result.Description.ShouldBe("Original");
        result.ContentRatings.ShouldHaveSingleItem().Descriptors.ShouldBe(["Original descriptor"]);
    }

    [Fact]
    public void Resolution_CallerMutatesRetainedCollections_OwnsProvenanceRatingsAndDescriptors()
    {
        string[] descriptors = ["Original"];
        var provenance = new Dictionary<string, string> { [nameof(Title.Description)] = "first" };
        ContentRating[] ratings =
        [new() { Board = RatingBoard.Esrb, Code = "T", Designation = RatingDesignation.Rated, MinimumAge = 13, SourceId = "first", Descriptors = descriptors }];
        var result = new TitleMetadataResolution("Description", null, null, null, null, null, null, provenance, ratings);

        provenance.Clear();
        descriptors[0] = "Changed";
        ratings[0] = ratings[0] with { Code = "M", MinimumAge = 17 };

        result.FieldProvenance[nameof(Title.Description)].ShouldBe("first");
        result.ContentRatings.ShouldHaveSingleItem().Code.ShouldBe("T");
        result.ContentRatings[0].Descriptors.ShouldBe(["Original"]);
        result.ConservativeMinimumAge.ShouldBe(13);
    }

    [Fact]
    public void LayerFacts_NullClaimAndDescriptorShapes_PreservesThemWhileOwningNonNullCollections()
    {
        var payload = JsonSerializer.Deserialize<TitleMetadataPayload>($$"""
            {"contentRatings":[null,
              {"board":{{(int)RatingBoard.Esrb}},"rawCode":"T","descriptors":null},
              {"board":{{(int)RatingBoard.Pegi}},"rawCode":"12","descriptors":["Original"]}]}
            """, MetadataJsonOptions.Default).ShouldNotBeNull();
        var claims = payload.ContentRatings.ShouldNotBeNull();
        var captured = Layer("provider", payload);

        Should.Throw<NotSupportedException>(() => ((IList<ContentRatingClaim>)claims)[0] = Claim(RatingBoard.Esrb, "M"));
        Should.Throw<NotSupportedException>(() => ((IList<string>)claims[2].Descriptors)[0] = "Changed");

        captured.ContentRatings[0].ShouldBeNull();
        captured.ContentRatings[1].ShouldNotBeNull().Descriptors.ShouldBeNull();
        captured.ContentRatings[2].ShouldNotBeNull().Descriptors.ShouldBe(["Original"]);
        Should.Throw<NotSupportedException>(() => ((ICollection<ContentRatingClaim?>)captured.ContentRatings).Clear());
    }
}
