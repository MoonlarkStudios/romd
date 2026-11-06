using NSubstitute;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Domain.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public class RegionResolverTests
{
    private readonly ITaxonomyRepository<Region> _repository = Substitute.For<ITaxonomyRepository<Region>>();
    private readonly TaxonomyResolver<Region> _resolver;
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public RegionResolverTests()
    {
        _resolver = new TaxonomyResolver<Region>(
            _repository,
            _unitOfWork,
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
        GivenAliases(aliases);

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
        GivenAliases(aliases);

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
        GivenAliases(aliases);

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
        GivenAliases(aliases);

        GivenStagedMint();

        // Act
        var result = await _resolver.ResolveAsync("Atlantis");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(42);
        await _repository.Received(1).AddStagedAsync(
            Arg.Is<Region>(r => r.Name == "Atlantis" && r.IsAutoCreated),
            "atlantis",
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_UnknownTokenAnotherIngestCreatedWhileItWaited_UsesThatRow()
    {
        // Arrange — the token is unknown until the minting lock is taken, by which time another ingest committed it.
        _repository.LockByAliasAsync("atlantis", Arg.Any<CancellationToken>()).Returns((int?)null, 42);

        // Act
        var result = await _resolver.ResolveAsync("Atlantis");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(42);
        Received.InOrder(() =>
        {
            _repository.LockByAliasAsync("atlantis", Arg.Any<CancellationToken>());
            _repository.AliasExistsAsync("atlantis", Arg.Any<CancellationToken>());
            _repository.LockMintingAsync(Arg.Any<CancellationToken>());
            _repository.LockByAliasAsync("atlantis", Arg.Any<CancellationToken>());
        });
        await _repository.DidNotReceive().AddStagedAsync(Arg.Any<Region>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_AliasAnotherWriterCommittedAfterTheReadUnderTheMintingLock_UsesThatWritersRow()
    {
        // Arrange — both reads miss, then the mint is refused: another writer committed the alias meanwhile.
        _repository.LockByAliasAsync("atlantis", Arg.Any<CancellationToken>()).Returns((int?)null, null, 7);
        GivenStagedMint();
        var conflict = new DbUpdateException("Alias conflict", new PostgresException("duplicate", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
        int flush = 0;
        _unitOfWork.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => ++flush == 2 ? Task.FromException(conflict) : Task.CompletedTask);
        _repository.IsAliasConflict(conflict).Returns(true);

        // Act
        var result = await _resolver.ResolveAsync("Atlantis");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(7);
        await _repository.Received(3).LockByAliasAsync("atlantis", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_AliasThatKeepsMovingWhileItIsRead_ReadsABoundedNumberOfTimes()
    {
        // Arrange — every read waited for a merge that moved the alias on.
        _repository.AliasExistsAsync("atlantis", Arg.Any<CancellationToken>()).Returns(true);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => _resolver.ResolveAsync("Atlantis"));
        await _repository.Received(3).LockByAliasAsync("atlantis", Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().LockMintingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_AliasAMergeMovedWhileItWasRead_ReadsAgainAndUsesTheTarget()
    {
        // Arrange — the first read waited for a merge of the alias's row, which moved the alias to row 9.
        _repository.LockByAliasAsync("atlantis", Arg.Any<CancellationToken>()).Returns((int?)null, 9);
        _repository.AliasExistsAsync("atlantis", Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _resolver.ResolveAsync("Atlantis");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(9);
        await _repository.DidNotReceive().LockMintingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive_ResolvesCorrectly()
    {
        // Arrange
        var aliases = new Dictionary<string, int> { ["japan"] = 3 };
        GivenAliases(aliases);

        // Act
        var result = await _resolver.ResolveAsync("JAPAN");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(3);
    }

    [Fact]
    public async Task ResolveAsync_TokenResolvedEarlierInTheIngest_IsNotReadAgain()
    {
        // Arrange
        GivenAliases(new Dictionary<string, int> { ["usa"] = 1 });
        await _resolver.ResolveAsync("USA");

        // Act
        var result = await _resolver.ResolveAsync("usa, USA");

        // Assert
        result.ShouldHaveSingleItem().ShouldBe(1);
        await _repository.Received(1).LockByAliasAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void GivenAliases(IReadOnlyDictionary<string, int> aliases) =>
        _repository.LockByAliasAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => aliases.TryGetValue(call.ArgAt<string>(0), out int id) ? id : null);

    [Fact]
    public async Task ResolveAsync_RowFlushUniqueFailure_PropagatesWithoutClassifyingOrAdoptingAnAliasWinner()
    {
        GivenStagedMint();
        var failure = new DbUpdateException("Canonical row key conflict", new PostgresException("duplicate", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
        _unitOfWork.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        _repository.IsAliasConflict(failure).Returns(true);

        (await Should.ThrowAsync<DbUpdateException>(() => _resolver.ResolveAsync("Atlantis"))).ShouldBeSameAs(failure);

        _repository.DidNotReceiveWithAnyArgs().IsAliasConflict(default!);
        await _repository.DidNotReceiveWithAnyArgs().AddAliasStagedAsync(default, default!, default);
    }

    private void GivenStagedMint()
    {
        var row = Substitute.For<IStagedTaxonomyRow>();
        row.ReadIdAfterFlush().Returns(42);
        var alias = Substitute.For<IStagedTaxonomyAlias>();
        alias.ReadAfterFlush().Returns(new TaxonomyAlias(3, "atlantis", "Installation"));
        _repository.AddStagedAsync(Arg.Any<Region>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(row);
        _repository.AddAliasStagedAsync(42, "atlantis", Arg.Any<CancellationToken>()).Returns(alias);
        _repository.ExecuteMintSavepointAsync(Arg.Any<Func<CancellationToken, Task<int?>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<int?>>>()(call.Arg<CancellationToken>()));
    }
}
