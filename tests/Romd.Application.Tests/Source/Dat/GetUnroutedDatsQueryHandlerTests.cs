using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Queries.GetUnroutedDats;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class GetUnroutedDatsQueryHandlerTests
{
    private readonly IDatRepository _repository = Substitute.For<IDatRepository>();

    private GetUnroutedDatsQueryHandler CreateHandler() => new(_repository);

    private static DatWithSourceStatus CreateUnroutedDat(
        int id,
        string name,
        CatalogSourceStatus sourceStatus,
        int catalogSourceId) =>
        new(
            DatFile.Rehydrate(
                id,
                name,
                name,
                null,
                null,
                null,
                DatType.NoIntro,
                null,
                $"{name}.dat",
                id,
                DateTimeOffset.UtcNow,
                null,
                0,
                0,
                0,
                id,
                DatFileLifecycle.Active,
                null),
            sourceStatus,
            catalogSourceId);

    [Fact]
    public async Task HandleAsync_NoUnroutedDats_ReturnsEmptyWithoutCounting()
    {
        _repository.GetUnroutedAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateHandler().HandleAsync(new GetUnroutedDatsQuery());

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeEmpty();
        await _repository.DidNotReceive()
            .CountMatchedRomFilesByDatAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnroutedDats_PairsEachWithItsBlockedCountAndSourceStatus()
    {
        // Source status and catalog id arrive on the repository rows themselves — there is
        // no second status lookup left to race a concurrent DAT deletion against.
        _repository.GetUnroutedAsync(Arg.Any<CancellationToken>())
            .Returns([
                CreateUnroutedDat(1, "Nintendo - Super Famicom", CatalogSourceStatus.Active, 11),
                CreateUnroutedDat(2, "Bandai - WonderSwan", CatalogSourceStatus.Disabled, 12)
            ]);
        _repository.CountMatchedRomFilesByDatAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [1] = 312 });

        var result = await CreateHandler().HandleAsync(new GetUnroutedDatsQuery());

        result.IsError.ShouldBeFalse();
        result.Value.Count.ShouldBe(2);
        result.Value[0].DatFile.Name.ShouldBe("Nintendo - Super Famicom");
        result.Value[0].MatchedRomFileCount.ShouldBe(312);
        result.Value[0].SourceStatus.ShouldBe(CatalogSourceStatus.Active);
        result.Value[0].CatalogSourceId.ShouldBe(11);
        // DATs with no matched files default to zero
        result.Value[1].MatchedRomFileCount.ShouldBe(0);
        result.Value[1].SourceStatus.ShouldBe(CatalogSourceStatus.Disabled);
        result.Value[1].CatalogSourceId.ShouldBe(12);
    }
}
