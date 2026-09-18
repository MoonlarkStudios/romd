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
        invalid.MarkConfigurationInvalid("bad config");
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns([valid, invalid]);

        var experience = Substitute.For<ILibraryExperienceRepository>();
        experience.GetCollectionIdsByLibraryAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<int, IReadOnlyList<string>> { [1] = [IdCoder.Encode(7)] });
        var result = await new ListLibrariesQueryHandler(TestSystemCatalog.Create(), repository, experience)
            .HandleAsync(new ListLibrariesQuery());

        result.IsError.ShouldBeFalse();
        result.Value[0].Id.ShouldBe(IdCoder.Encode(1));
        result.Value[0].Configuration.ShouldNotBeNull();
        result.Value[0].CollectionIds.ShouldBe([IdCoder.Encode(7)]);
        result.Value[1].CollectionIds.ShouldBeEmpty();
        result.Value[1].Id.ShouldBe(IdCoder.Encode(2));
        result.Value[1].Configuration.ShouldBeNull();
        result.Value[1].ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        result.Value[1].ConfigurationError.ShouldBe("bad config");
    }

    [Fact]
    public async Task GetLibraryById_Missing_ReturnsOpaqueNotFound()
    {
        var repository = Substitute.For<ILibraryRepository>();

        var result = await new GetLibraryByIdQueryHandler(TestSystemCatalog.Create(), repository)
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
        repository.GetGenreFacetsAsync(7, 2, Arg.Any<CancellationToken>())
            .Returns([new GenreFacet("RPG", 5)]);

        var result = await new GetLibraryGenreFacetsQueryHandler(repository)
            .HandleAsync(new GetLibraryGenreFacetsQuery(7, 2));

        result.IsError.ShouldBeFalse();
        result.Value.Single().Id.ShouldBe("RPG");
        result.Value.Single().Name.ShouldBe("RPG");
        result.Value.Single().Count.ShouldBe(5);
    }

    [Fact]
    public async Task GetCollections_ExistingLibrary_MapsOpaqueCollectionIdAndCounts()
    {
        var repository = Substitute.For<ILibraryRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(NewLibrary(7, "Library"));
        repository.GetCollectionFacetsAsync(7, 1, Arg.Any<CancellationToken>())
            .Returns([new CollectionFacet(11, "Favorites", 3, 8)]);

        var result = await new GetLibraryCollectionsQueryHandler(repository)
            .HandleAsync(new GetLibraryCollectionsQuery(7, 1));

        result.IsError.ShouldBeFalse();
        var collection = result.Value.Single();
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
        repository.GetTitleReleaseDiagnosticsAsync(7, 13, Arg.Any<CancellationToken>())
            .Returns([
                new LibraryTitleReleaseDiagnostics(
                    17,
                    19,
                    "Game (USA)",
                    true,
                    false,
                    null,
                    true,
                    "ExposedDefault")
            ]);

        var result = await new GetLibraryTitleReleaseDiagnosticsQueryHandler(repository)
            .HandleAsync(new GetLibraryTitleReleaseDiagnosticsQuery(7, 13));

        result.IsError.ShouldBeFalse();
        var release = result.Value.Single();
        release.Id.ShouldBe(IdCoder.Encode(17));
        release.DatId.ShouldBe(IdCoder.Encode(19));
        release.ExposureReason.ShouldBe("ExposedDefault");
    }

    private static Library NewLibrary(int id, string name)
    {
        var method = typeof(Library).GetMethod(
            "Rehydrate",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        return method.Invoke(null,
        [
            id,
            name,
            new LibraryConfiguration(),
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
