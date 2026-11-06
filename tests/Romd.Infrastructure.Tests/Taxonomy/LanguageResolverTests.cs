using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Domain.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public class LanguageResolverTests
{
    private readonly ITaxonomyRepository<GameLanguage> _repository = Substitute.For<ITaxonomyRepository<GameLanguage>>();
    private readonly TaxonomyResolver<GameLanguage> _resolver;
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public LanguageResolverTests()
    {
        _resolver = new TaxonomyResolver<GameLanguage>(
            _repository,
            _unitOfWork,
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
        GivenAliases(aliases);

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
        GivenAliases(aliases);

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
        GivenAliases(aliases);

        var row = Substitute.For<IStagedTaxonomyRow>();
        row.ReadIdAfterFlush().Returns(99);
        var alias = Substitute.For<IStagedTaxonomyAlias>();
        alias.ReadAfterFlush().Returns(new TaxonomyAlias(3, "klingon", "Installation"));
        _repository.AddStagedAsync(Arg.Any<GameLanguage>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(row);
        _repository.AddAliasStagedAsync(99, "klingon", Arg.Any<CancellationToken>()).Returns(alias);
        _repository.ExecuteMintSavepointAsync(Arg.Any<Func<CancellationToken, Task<int?>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<int?>>>()(call.Arg<CancellationToken>()));

        // Act
        var result = await _resolver.ResolveAsync("Klingon");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(99);
        await _repository.Received(1).AddStagedAsync(
            Arg.Is<GameLanguage>(l => l.Name == "Klingon" && l.IsAutoCreated),
            "klingon",
            Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _ = _repository.AddStagedAsync(Arg.Any<GameLanguage>(), "klingon", Arg.Any<CancellationToken>());
            _ = _unitOfWork.FlushAsync(Arg.Any<CancellationToken>());
            row.ReadIdAfterFlush();
            _ = _repository.AddAliasStagedAsync(99, "klingon", Arg.Any<CancellationToken>());
            _ = _unitOfWork.FlushAsync(Arg.Any<CancellationToken>());
            alias.ReadAfterFlush();
        });
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive_ResolvesCorrectly()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["ja"] = 2 };
        GivenAliases(aliases);

        // Act
        var result = await _resolver.ResolveAsync("JA");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(2);
    }

    private void GivenAliases(IReadOnlyDictionary<string, int> aliases) =>
        _repository.LockByAliasAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => aliases.TryGetValue(call.ArgAt<string>(0), out int id) ? id : null);
}
