using NSubstitute;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public class LanguageResolverTests
{
    private readonly ITaxonomyRepository<GameLanguage> _repository = Substitute.For<ITaxonomyRepository<GameLanguage>>();
    private readonly TaxonomyAliasCache _cache = new();
    private readonly TaxonomyResolver<GameLanguage> _resolver;

    public LanguageResolverTests()
    {
        _resolver = new TaxonomyResolver<GameLanguage>(
            _repository,
            _cache,
            cacheKey: "languages",
            entityFactory: token => GameLanguage.CreateNew(token, token.ToLowerInvariant(), sortOrder: 999, isAutoCreated: true));
    }

    [Fact]
    public async Task ResolveAsync_NullInput_ReturnsEmpty()
    {
        var result = await _resolver.ResolveAsync(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_SingleKnownAlias_ReturnsLanguageId()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["en"] = 1, ["english"] = 1 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("En");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_CommaSeparatedInput_ReturnsMultipleDistinctIds()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["en"] = 1, ["fr"] = 2, ["de"] = 3 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("En,Fr,De");

        // Assert
        result.Count.ShouldBe(3);
        result.ShouldContain(1);
        result.ShouldContain(2);
        result.ShouldContain(3);
    }

    [Fact]
    public async Task ResolveAsync_UnknownToken_AutoCreatesLanguageAndAlias()
    {
        // Arrange
        var aliases = new Dictionary<string, int>();
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        _repository.AddAsync(Arg.Any<GameLanguage>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var lang = callInfo.Arg<GameLanguage>();
                return GameLanguage.Rehydrate(99, lang.Name, lang.Code, lang.SortOrder, lang.IsAutoCreated);
            });

        // Act
        var result = await _resolver.ResolveAsync("Klingon");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(99);
        await _repository.Received(1).AddAsync(
            Arg.Is<GameLanguage>(l => l.Name == "Klingon" && l.IsAutoCreated),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAliasAsync(99, "klingon", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive_ResolvesCorrectly()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["ja"] = 2 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases);

        // Act
        var result = await _resolver.ResolveAsync("JA");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(2);
    }

    [Fact]
    public async Task InvalidateCache_ForcesReload()
    {
        // Arrange — load cache initially
        var aliases1 = new Dictionary<string, int> { ["en"] = 1 };
        _repository.GetAllAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(aliases1);

        await _resolver.ResolveAsync("En");
        _cache.IsLoaded("languages").ShouldBeTrue();

        // Act
        _resolver.InvalidateCache();

        // Assert
        _cache.IsLoaded("languages").ShouldBeFalse();
    }
}
