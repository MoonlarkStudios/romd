using Romd.Application.Common.Pagination;
using System.Reflection;
using NSubstitute;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Queries.GetLibraryById;
using Romd.Admin.Application.Libraries.Queries.GetLibraryCollections;
using Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryPlatformFacets;
using Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;
using Romd.Admin.Application.Libraries.Queries.ListLibraries;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Application.Tests.Content;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Libraries;

public sealed class LibraryQueryHandlerTests
{
    [Fact]
    public async Task ListLibraries_ValidAndInvalidConfigurations_MapExistingContractShape()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var valid = NewLibrary(1, "Valid");
        var invalid = NewLibrary(2, "Invalid");
        invalid.MarkConfigurationInvalid("bad config", now: DateTimeOffset.UnixEpoch);
        repository.GetPageAsync(null, null, 50, Arg.Any<CancellationToken>()).Returns(new PagedList<Library>([valid, invalid], null, false));

        var experience = Substitute.For<ILibraryExperienceRepository>();
        experience.GetCollectionIdsByLibraryAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<int>>()).Returns(
            new Dictionary<int, IReadOnlyList<string>> { [1] = [IdCoder.Encode(7)] });
        var result = await new ListLibrariesQueryHandler(TestSystemCatalog.Create(), repository, experience, TestDeliveryPolicies.Production)
            .HandleAsync(new ListLibrariesQuery());

        result.IsError.ShouldBeFalse();
        result.Value.Items[0].Id.ShouldBe(IdCoder.Encode(1));
        result.Value.Items[0].Configuration.ShouldNotBeNull();
        result.Value.Items[0].CollectionIds.ShouldBe([IdCoder.Encode(7)]);
        result.Value.Items[1].CollectionIds.ShouldBeEmpty();
        result.Value.Items[1].Id.ShouldBe(IdCoder.Encode(2));
        result.Value.Items[1].Configuration.ShouldBeNull();
        result.Value.Items[1].ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        result.Value.Items[1].ConfigurationError.ShouldBe("bad config");
    }

    [Theory]
    [InlineData(new int[0], new[] { "original:Original", "rvz:RVZ", "chd:CHD" })]
    [InlineData(new[] { 2 }, new[] { "original:Original" })]
    [InlineData(new[] { 2, 1 }, new[] { "original:Original", "rvz:RVZ" })]
    [InlineData(new[] { 3, 1 }, new[] { "original:Original", "rvz:RVZ", "chd:CHD" })]
    public async Task GetLibraryById_AllowedSystems_ListsOriginalThenTheirStoredFormats(int[] systemIds, string[] expected)
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library",
            new LibraryConfiguration { AllowedPlatformIds = systemIds }));

        var result = await new GetLibraryByIdQueryHandler(MediaSystems(), repository, TestDeliveryPolicies.Production)
            .HandleAsync(new GetLibraryByIdQuery(7));

        result.Value.ExportFormats.Select(format => $"{format.Format}:{format.Label}").ShouldBe(expected);
    }

    [Fact]
    public async Task ListLibraries_InvalidConfiguration_OffersOnlyOriginalExport()
    {
        var repository = Substitute.For<ILibraryRepository>();
        var valid = NewLibrary(1, "Valid", new LibraryConfiguration { AllowedPlatformIds = [1] });
        var invalid = NewLibrary(2, "Invalid", new LibraryConfiguration { AllowedPlatformIds = [1] });
        invalid.MarkConfigurationInvalid("bad config", now: DateTimeOffset.UnixEpoch);
        repository.GetPageAsync(null, null, 50, Arg.Any<CancellationToken>()).Returns(new PagedList<Library>([valid, invalid], null, false));
        var experience = Substitute.For<ILibraryExperienceRepository>();
        experience.GetCollectionIdsByLibraryAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<int>>())
            .Returns(new Dictionary<int, IReadOnlyList<string>>());

        var result = await new ListLibrariesQueryHandler(MediaSystems(), repository, experience, TestDeliveryPolicies.Production)
            .HandleAsync(new ListLibrariesQuery());

        result.Value.Items.Select(library => string.Join(",", library.ExportFormats.Select(format => format.Format)))
            .ShouldBe(["original,rvz", "original"]);
    }

    [Fact]
    public async Task GetLibraryById_Missing_ReturnsOpaqueNotFound()
    {
        var repository = Substitute.For<ILibraryRepository>();

        var result = await new GetLibraryByIdQueryHandler(TestSystemCatalog.Create(), repository, TestDeliveryPolicies.Production)
            .HandleAsync(new GetLibraryByIdQuery(123));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.NotFound");
        result.FirstError.Description.ShouldNotContain("123");
    }

    [Fact]
    public async Task GetPlatformFacets_ExistingLibrary_MapsIdsAndMinimumItems()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library"));
        repository.GetPlatformFacetsAsync(7, 3, Arg.Any<CancellationToken>())
            .Returns([new PlatformFacet(9, "Arcade", 4)]);

        var result = await new GetLibraryPlatformFacetsQueryHandler(TestSystemCatalog.Create(), repository)
            .HandleAsync(new GetLibraryPlatformFacetsQuery(7, 3));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe([new("arcade", "Arcade", 4)]);
    }

    [Fact]
    public async Task GetGenreFacets_ExistingLibrary_MapsGenreAsStableIdAndName()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library"));
        repository.GetGenrePageAsync(7, 2, null, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedList<GenreFacet>([new GenreFacet("RPG", 5)], null, false));

        var result = await new GetLibraryGenreFacetsQueryHandler(repository)
            .HandleAsync(new GetLibraryGenreFacetsQuery(7, 2));

        result.IsError.ShouldBeFalse();
        result.Value.Items.Single().Id.ShouldBe("RPG");
        result.Value.Items.Single().Name.ShouldBe("RPG");
        result.Value.Items.Single().Count.ShouldBe(5);
    }

    [Fact]
    public async Task GetCollections_ExistingLibrary_MapsOpaqueCollectionIdAndCounts()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library"));
        repository.GetCollectionPageAsync(7, 1, null, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedList<CollectionFacet>([new CollectionFacet(11, "Favorites", 3, 8)], null, false));

        var result = await new GetLibraryCollectionsQueryHandler(repository)
            .HandleAsync(new GetLibraryCollectionsQuery(7, 1));

        result.IsError.ShouldBeFalse();
        var collection = result.Value.Items.Single();
        collection.Id.ShouldBe(IdCoder.Encode(11));
        collection.Name.ShouldBe("Favorites");
        collection.MatchingCount.ShouldBe(3);
        collection.TotalCount.ShouldBe(8);
    }

    [Fact]
    public async Task GetTitleReleaseDiagnostics_ExistingLibrary_MapsOpaqueReleaseAndDatIds()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library"));
        repository.GetReleaseDiagnosticsPageAsync(7, 13, null, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedList<LibraryTitleReleaseDiagnostics>([
                new LibraryTitleReleaseDiagnostics(
                    17,
                    19,
                    "Game (USA)",
                    true,
                    false,
                    null,
                    true,
                    "ExposedDefault")
            ], null, false));

        var result = await new GetLibraryTitleReleaseDiagnosticsQueryHandler(repository)
            .HandleAsync(new GetLibraryTitleReleaseDiagnosticsQuery(7, 13));

        result.IsError.ShouldBeFalse();
        var release = result.Value.Items.Single();
        release.Id.ShouldBe(IdCoder.Encode(17));
        release.DatId.ShouldBe(IdCoder.Encode(19));
        release.ExposureReason.ShouldBe("ExposedDefault");
    }

    /// <summary>Reference systems that include media-policy systems, under ids unlike their keys.</summary>
    private static IReferenceCatalogService MediaSystems()
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.GetSystemKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new SystemKeys(new Dictionary<int, string> { [1] = "gc", [2] = "snes", [3] = "ps2" }));
        return catalog;
    }

    private static Library NewLibrary(int id, string name, LibraryConfiguration? configuration = null)
    {
        var method = typeof(Library).GetMethod(
            "Rehydrate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return method.Invoke(null,
        [
            id,
            name,
            configuration ?? new LibraryConfiguration(),
            LibraryConfigurationState.Valid,
            null,
            false,
            true,
            null,
            0,
            DateTimeOffset.UtcNow,
            null
        ]).ShouldBeOfType<Library>();
    }
}
