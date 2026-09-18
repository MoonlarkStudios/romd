using NSubstitute;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public class RegionResolverTests
{
    private readonly ITaxonomyRepository<Region> _repository = Substitute.For<ITaxonomyRepository<Region>>();
    private readonly TaxonomyAliasCache _cache = new();
    private readonly TaxonomyResolver<Region> _resolver;

    public RegionResolverTests()
    {
        _resolver = new TaxonomyResolver<Region>(
            _repository,
            _cache,
            cacheKey: "regions",
            entityFactory: token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true));
    }

    [Fact]
    public async Task ResolveAsync_NullInput_ReturnsEmpty()
    {
        var result = await _resolver.ResolveAsync(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceInput_ReturnsEmpty()
    {
        var result = await _resolver.ResolveAsync("   ");

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_SingleKnownAlias_ReturnsRegionId()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["usa"] = 1, ["us"] = 1 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("USA");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_CommaSeparatedInput_ReturnsMultipleDistinctIds()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["usa"] = 1, ["europe"] = 2, ["japan"] = 3 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("USA, Europe, Japan");

        // Assert
        result.Count.ShouldBe(3);
        result.ShouldContain(1);
        result.ShouldContain(2);
        result.ShouldContain(3);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateTokens_ReturnsDistinctIds()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["usa"] = 1, ["us"] = 1 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("USA, US");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_UnknownToken_AutoCreatesRegionAndAlias()
    {
        // Arrange
        var aliases = new Dictionary<string, int>();
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        _repository.AddAsync(Arg.Any<Region>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var region = callInfo.Arg<Region>();
                return Region.Rehydrate(42, region.Name, region.SortOrder, region.IsAutoCreated);
            });

        // Act
        var result = await _resolver.ResolveAsync("Atlantis");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(42);
        await _repository.Received(1).AddAsync(
            Arg.Is<Region>(r => r.Name == "Atlantis" && r.IsAutoCreated),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAliasAsync(42, "atlantis", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive_ResolvesCorrectly()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["japan"] = 3 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("JAPAN");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(3);
    }

    [Fact]
    public async Task InvalidateCache_ForcesReload()
    {
        // Arrange — load cache initially
        var aliases1 = new Dictionary<string, int> { ["usa"] = 1 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases1);

        await _resolver.ResolveAsync("USA");
        _cache.IsLoaded("regions").ShouldBeTrue();

        // Act
        _resolver.InvalidateCache();

        // Assert
        _cache.IsLoaded("regions").ShouldBeFalse();

        // Next resolve should reload
        var aliases2 = new Dictionary<string, int> { ["usa"] = 1, ["eu"] = 2 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases2);

        var result = await _resolver.ResolveAsync("EU");
        result.ShouldHaveSingleItem().ShouldBe(2);
    }
}
