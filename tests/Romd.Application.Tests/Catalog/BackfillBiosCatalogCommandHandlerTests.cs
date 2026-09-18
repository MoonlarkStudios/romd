using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;
using Romd.Admin.Application.Common.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class BackfillBiosCatalogCommandHandlerTests
{
    private readonly IBiosRepository _biosRepository = Substitute.For<IBiosRepository>();
    private readonly IBiosGrouper _biosGrouper = Substitute.For<IBiosGrouper>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    [Fact]
    public async Task HandleAsync_GroupsUnmappedGamesPerPlatform()
    {
        _biosRepository.GetUnmappedBiosGamesAsync(Arg.Any<CancellationToken>()).Returns(new List<UnmappedBiosGame>
        {
            new(101, 1, "[BIOS] PSX (USA)"),
            new(102, 1, "[BIOS] PSX (Japan)"),
            new(201, 2, "[BIOS] Saturn (USA)")
        });
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        _biosGrouper.GroupAsync(1, Arg.Any<IReadOnlyList<(int, string)>>(), Arg.Any<CancellationToken>()).Returns(2);
        _biosGrouper.GroupAsync(2, Arg.Any<IReadOnlyList<(int, string)>>(), Arg.Any<CancellationToken>()).Returns(1);

        var handler = CreateHandler();

        var result = await handler.HandleAsync(new BackfillBiosCatalogCommand());

        result.IsError.ShouldBeFalse();
        result.Value.BiosEntriesCreated.ShouldBe(3);
        result.Value.GamesGrouped.ShouldBe(3);

        await _biosGrouper.Received(1).GroupAsync(
            1,
            Arg.Is<IReadOnlyList<(int GameId, string Name)>>(g => g.Count == 2),
            Arg.Any<CancellationToken>());
        await _biosGrouper.Received(1).GroupAsync(
            2,
            Arg.Is<IReadOnlyList<(int GameId, string Name)>>(g => g.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NothingUnmapped_ReturnsZeroWithoutTransaction()
    {
        _biosRepository.GetUnmappedBiosGamesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateHandler().HandleAsync(new BackfillBiosCatalogCommand());

        result.IsError.ShouldBeFalse();
        result.Value.BiosEntriesCreated.ShouldBe(0);
        result.Value.GamesGrouped.ShouldBe(0);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    private BackfillBiosCatalogCommandHandler CreateHandler() =>
        new(_biosRepository, _biosGrouper, _unitOfWork, NullLogger<BackfillBiosCatalogCommandHandler>.Instance);
}
