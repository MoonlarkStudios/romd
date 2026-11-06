using NSubstitute;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetTitlesTracked;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class SetTitlesTrackedCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_DelegatesToBulkRepositoryWithIdsAndFlag()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        var handler = new SetTitlesTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitlesTrackedCommand([1, 2, 3], null, null, Tracked: true));

        result.IsError.ShouldBeFalse();
        await repository.Received(1).TrackByTitleIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Untrack_PassesFalse()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        var handler = new SetTitlesTrackedCommandHandler(repository);

        await handler.HandleAsync(new SetTitlesTrackedCommand([5], null, null, Tracked: false));

        await repository.Received(1).UntrackByTitleIdsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_PlatformSelector_Delegates(bool tracked)
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        var handler = new SetTitlesTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitlesTrackedCommand(null, 7, null, tracked));

        result.IsError.ShouldBeFalse();
        if (tracked)
            await repository.Received(1).TrackByPlatformAsync(7, Arg.Any<CancellationToken>());
        else
            await repository.Received(1).UntrackByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DatSourceSelector_Delegates()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        var handler = new SetTitlesTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitlesTrackedCommand(null, null, 9, true));

        result.IsError.ShouldBeFalse();
        await repository.Received(1).TrackByDatSourceAsync(9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MultipleSelectors_ReturnsValidationError()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        var handler = new SetTitlesTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitlesTrackedCommand([1], 7, null, true));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.InvalidTrackingSelector");
    }
}
