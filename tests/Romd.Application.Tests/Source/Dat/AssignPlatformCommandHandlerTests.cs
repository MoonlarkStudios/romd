using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;
using DomainPlatform = Romd.Domain.Source.Platform.Platform;

namespace Romd.Application.Tests.Source.Dat;

public sealed class AssignPlatformCommandHandlerTests
{
    private const int DatId = 5;
    private const int PlatformId = 3;
    private const int CatalogSourceId = 77;

    private readonly IDatRepository _datRepository = Substitute.For<IDatRepository>();
    private readonly FakeTitleDerivationService _titleDerivation = new();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();
    private readonly IBiosGrouper _biosGrouper = Substitute.For<IBiosGrouper>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILibraryRepository _libraryRepository = Substitute.For<ILibraryRepository>();
    private readonly ICatalogProjectionService _catalogProjection = Substitute.For<ICatalogProjectionService>();
    private readonly ISourceLifecycle _sourceLifecycle = Substitute.For<ISourceLifecycle>();

    [Fact]
    public async Task HandleAsync_DelegatesOnlyBiosGamesToGrouperAndClaimsEveryGame()
    {
        SetupHandlerDependencies([
            BiosGame(101, "[BIOS] PSX (USA)"),
            BiosGame(103, "[BIOS] PSX (Japan)"),
            NonBiosGame(104, "Some Game (USA)")
        ]);

        var result = await CreateHandler().HandleAsync(AssignPlatformCommand.Create(DatId, PlatformId).Value);

        result.IsError.ShouldBeFalse();

        var grouped = GroupedGamesPassedToGrouper();
        grouped.Select(g => g.GameId).OrderBy(id => id).ShouldBe([101, 103]);
        grouped.ShouldContain((101, "[BIOS] PSX (USA)"));
        grouped.ShouldContain((103, "[BIOS] PSX (Japan)"));

        // Every game — BIOS included — becomes a platformful claim through the derivation door.
        _titleDerivation.UpsertClaims.ShouldAllBe(c => c.CatalogSourceId == CatalogSourceId);
        _titleDerivation.UpsertClaims
            .Select(c => (c.Claim.EntryKey, c.Claim.PlatformId, c.Claim.IsBios))
            .ShouldBe([
                ("[BIOS] PSX (USA)", PlatformId, true),
                ("[BIOS] PSX (Japan)", PlatformId, true),
                ("Some Game (USA)", PlatformId, false)
            ]);

        // Standard case: every non-BIOS game linked, so counts match pre-facade semantics.
        result.Value.GamesUpdated.ShouldBe(1);
        result.Value.NewTitlesCreated.ShouldBe(0);
        result.Value.ExistingTitlesMatched.ShouldBe(1);
    }

    [Fact]
    public async Task HandleAsync_NoBiosGames_DelegatesEmptyListToGrouper()
    {
        SetupHandlerDependencies([NonBiosGame(1, "Some Game (USA)")]);

        var result = await CreateHandler().HandleAsync(AssignPlatformCommand.Create(DatId, PlatformId).Value);

        result.IsError.ShouldBeFalse();
        GroupedGamesPassedToGrouper().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        SetupHandlerDependencies([NonBiosGame(1, "Some Game (USA)")]);
        _catalogProjection.RebuildPlatformAsync(PlatformId, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("rebuild interrupted"));

        var result = await CreateHandler().HandleAsync(AssignPlatformCommand.Create(DatId, PlatformId).Value);

        result.IsError.ShouldBeFalse();
        await _catalogProjection.Received(1)
            .RefreshCatalogSourcePayloadAsync(CatalogSourceId, Arg.Any<CancellationToken>());
        await _catalogProjection.Received(1).MarkPlatformDirtyAsync(
            PlatformId,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.Count == 0));
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(PlatformId, Arg.Any<CancellationToken>());
    }

    private IReadOnlyList<(int GameId, string Name)> GroupedGamesPassedToGrouper()
    {
        var call = _biosGrouper.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IBiosGrouper.GroupAsync));
        return (IReadOnlyList<(int GameId, string Name)>)call.GetArguments()[1]!;
    }

    private void SetupHandlerDependencies(IReadOnlyList<DatGame> games)
    {
        var datFile = DatFile.CreateNew("PSX", "PSX DAT", DatType.NoIntro, "psx.dat", fileId: 1);
        _datRepository.GetByIdAsync(DatId, Arg.Any<CancellationToken>()).Returns(datFile);
        _datRepository.GetAllGamesByDatIdAsync(DatId, BiosFilter.Include, Arg.Any<CancellationToken>())
            .Returns(games);
        _datRepository.GetCatalogSourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(CatalogSourceId);

        _platformRepository.GetByIdAsync(PlatformId, Arg.Any<CancellationToken>())
            .Returns(DomainPlatform.CreateNew("Sony PlayStation", "psx"));

        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        _sourceLifecycle.GetLinkedTitleIdsAsync(CatalogSourceId, Arg.Any<CancellationToken>())
            .Returns([]);

        _catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private AssignPlatformCommandHandler CreateHandler() =>
        new(
            _datRepository,
            _titleDerivation,
            _platformRepository,
            _biosGrouper,
            _unitOfWork,
            _libraryRepository,
            _catalogProjection,
            NullLogger<AssignPlatformCommandHandler>.Instance,
            _sourceLifecycle);

    private static DatGame BiosGame(int id, string name) =>
        DatGame.Rehydrate(id, DatId, name, null, null, null, null, null, null, null, "BIOS", null, null, true, [], []);

    private static DatGame NonBiosGame(int id, string name) =>
        DatGame.Rehydrate(id, DatId, name, null, null, null, null, null, null, null, "Games", null, null, false, [], []);
}
