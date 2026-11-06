using System.Text.Json;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Libraries;

public sealed class LibraryConfigurationEncapsulationTests
{
    [Fact]
    public void DefaultPolicy_ImmutableBoardPreference_PreservesDefaultValueEquality()
    {
        var first = new ContentRatingPolicy();
        var second = new ContentRatingPolicy();

        first.ShouldBe(second);
        first.BoardPreference.ShouldBe(RatingBoardCatalog.DefaultBoardPreference);
        Should.Throw<NotSupportedException>(() => ((IList<RatingBoard>)first.BoardPreference)[0] = RatingBoard.Pegi);
        second.BoardPreference.ShouldBe(RatingBoardCatalog.DefaultBoardPreference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_AfterCreateOrUpdate_OwnsEverySuppliedCollection(bool update)
    {
        var platforms = new List<int> { 1 };
        var dats = new List<int> { 2 };
        var genres = new List<string> { "Adventure" };
        var discSets = new List<int> { 3 };
        var included = new List<int> { 4 };
        var excluded = new List<int> { 5 };
        var boards = new List<RatingBoard> { RatingBoard.Esrb };
        var configuration = new LibraryConfiguration
        {
            AllowedPlatformIds = platforms,
            ExcludedDatIds = dats,
            AllowedGenres = genres,
            SelectedDiscSetIds = discSets,
            IncludeTitleIds = included,
            ExcludeTitleIds = excluded,
            ContentRatingPolicy = new ContentRatingPolicy { BoardPreference = boards }
        };
        var library = Library.CreateNew("Library", update ? new LibraryConfiguration() : configuration, now: DateTimeOffset.UnixEpoch);
        if (update)
            library.UpdateConfiguration("Updated", configuration, now: DateTimeOffset.UnixEpoch);
        library.MarkMaterialized(10, now: DateTimeOffset.UnixEpoch);
        var revision = library.MaterializationRevision;
        var serialized = JsonSerializer.Serialize(library.Configuration);

        platforms.Clear();
        dats.Clear();
        genres.Clear();
        discSets.Clear();
        included.Clear();
        excluded.Clear();
        boards.Clear();

        library.Configuration.AllowedPlatformIds.ShouldBe([1]);
        library.Configuration.ExcludedDatIds.ShouldBe([2]);
        library.Configuration.AllowedGenres.ShouldBe(["Adventure"]);
        library.Configuration.SelectedDiscSetIds.ShouldBe([3]);
        library.Configuration.IncludeTitleIds.ShouldBe([4]);
        library.Configuration.ExcludeTitleIds.ShouldBe([5]);
        library.Configuration.ContentRatingPolicy.BoardPreference.ShouldBe([RatingBoard.Esrb]);
        JsonSerializer.Serialize(library.Configuration).ShouldBe(serialized);
        library.MaterializationRevision.ShouldBe(revision);
        library.NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public void ConfigurationCollections_ThroughMutableInterfaces_RejectChanges()
    {
        var configuration = new LibraryConfiguration
        {
            AllowedPlatformIds = [1],
            ExcludedDatIds = [2],
            AllowedGenres = ["Adventure"],
            SelectedDiscSetIds = [3],
            IncludeTitleIds = [4],
            ExcludeTitleIds = [5],
            ContentRatingPolicy = new ContentRatingPolicy { BoardPreference = [RatingBoard.Esrb] }
        };

        Should.Throw<NotSupportedException>(() => ((IList<int>)configuration.AllowedPlatformIds)[0] = 9);
        Should.Throw<NotSupportedException>(() => ((IList<int>)configuration.ExcludedDatIds)[0] = 9);
        Should.Throw<NotSupportedException>(() => ((IList<string>)configuration.AllowedGenres)[0] = "Changed");
        Should.Throw<NotSupportedException>(() => ((IList<int>)configuration.SelectedDiscSetIds)[0] = 9);
        Should.Throw<NotSupportedException>(() => ((IList<int>)configuration.IncludeTitleIds)[0] = 9);
        Should.Throw<NotSupportedException>(() => ((IList<int>)configuration.ExcludeTitleIds)[0] = 9);
        Should.Throw<NotSupportedException>(() => ((IList<RatingBoard>)configuration.ContentRatingPolicy.BoardPreference)[0] = RatingBoard.Pegi);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("discSet")]
    [InlineData("policy")]
    [InlineData("boards")]
    [InlineData("genrePolicy")]
    public void CreateNew_InvalidConfiguration_RejectsBeforeMarkingValid(string invalidPart)
    {
        var error = Should.Throw<ArgumentException>(() => Library.CreateNew("Library", InvalidConfiguration(invalidPart), now: DateTimeOffset.UnixEpoch));

        error.ParamName.ShouldBe("configuration");
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("discSet")]
    [InlineData("policy")]
    [InlineData("boards")]
    [InlineData("genrePolicy")]
    public void UpdateConfiguration_InvalidConfiguration_PreservesEntirePublishedState(string invalidPart)
    {
        var library = Library.CreateNew("Library", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch);
        library.MarkMaterialized(10, now: DateTimeOffset.UnixEpoch);
        var configuration = library.Configuration;
        var revision = library.MaterializationRevision;
        var publishedAt = library.LastMaterializedAt;
        var updatedAt = library.UpdatedAt;

        Should.Throw<ArgumentException>(() => library.UpdateConfiguration("Changed", InvalidConfiguration(invalidPart), now: DateTimeOffset.UnixEpoch));

        library.Name.ShouldBe("Library");
        library.Configuration.ShouldBeSameAs(configuration);
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Valid);
        library.ConfigurationError.ShouldBeNull();
        library.MaterializationRevision.ShouldBe(revision);
        library.NeedsMaterialization.ShouldBeFalse();
        library.LastMaterializedAt.ShouldBe(publishedAt);
        library.UpdatedAt.ShouldBe(updatedAt);
        library.ItemCount.ShouldBe(10);
    }

    [Fact]
    public void Configuration_JsonRoundTrip_PreservesPolicyShapeAndValidation()
    {
        var configuration = new LibraryConfiguration
        {
            AllowedPlatformIds = [1],
            ExcludedDatIds = [2],
            AllowedGenres = ["Adventure"],
            SelectedDiscSetIds = [3],
            IncludeTitleIds = [4],
            ExcludeTitleIds = [5],
            ContentRatingPolicy = new ContentRatingPolicy { BoardPreference = [RatingBoard.Esrb] }
        };
        var json = JsonSerializer.Serialize(configuration);

        var restored = JsonSerializer.Deserialize<LibraryConfiguration>(json).ShouldNotBeNull();

        JsonSerializer.Serialize(restored).ShouldBe(json);
        LibraryConfigurationValidator.Validate(restored).ShouldBeNull();
        Should.Throw<NotSupportedException>(() => ((IList<int>)restored.AllowedPlatformIds)[0] = 9);
    }

    [Theory]
    [InlineData("{\"AllowedPlatformIds\":null}", "Allowed platform IDs are required.")]
    [InlineData("{\"ContentRatingPolicy\":null}", "Content rating policy is required.")]
    [InlineData("{\"ContentRatingPolicy\":{\"BoardPreference\":null}}", "Rating board preference is required.")]
    public void Configuration_ExplicitJsonNulls_PreserveFailClosedValidation(string json, string expectedError)
    {
        var restored = JsonSerializer.Deserialize<LibraryConfiguration>(json).ShouldNotBeNull();

        LibraryConfigurationValidator.Validate(restored).ShouldBe(expectedError);
    }

    private static LibraryConfiguration InvalidConfiguration(string part) => part switch
    {
        "platform" => new() { AllowedPlatformIds = [0] },
        "discSet" => new() { SelectedDiscSetIds = [1, 1] },
        "policy" => new() { ContentRatingPolicy = null! },
        "boards" => new() { ContentRatingPolicy = new ContentRatingPolicy { BoardPreference = [] } },
        "genrePolicy" => new() { UnknownGenrePolicy = (UnknownMetadataPolicy)99 },
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}
