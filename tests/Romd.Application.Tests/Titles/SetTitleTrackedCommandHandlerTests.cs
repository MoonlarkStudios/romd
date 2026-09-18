using NSubstitute;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetTitleTracked;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class SetTitleTrackedCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExistingTitle_SetsTrackedAndReturnsUpdated()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        repository.TrackAsync(1, null, Arg.Any<CancellationToken>()).Returns(TrackTitleResult.Updated);
        var handler = new SetTitleTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitleTrackedCommand(1, true));

        result.IsError.ShouldBeFalse();
        await repository.Received(1).TrackAsync(1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Untrack_PassesFalseThrough()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        repository.UntrackAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new SetTitleTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitleTrackedCommand(1, false));

        result.IsError.ShouldBeFalse();
        await repository.Received(1).UntrackAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingTitle_ReturnsNotFound()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        repository.TrackAsync(99, null, Arg.Any<CancellationToken>()).Returns(TrackTitleResult.TitleNotFound);
        var handler = new SetTitleTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitleTrackedCommand(99, true));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
    }

    [Fact]
    public async Task HandleAsync_PinForDifferentTitle_ReturnsValidationError()
    {
        var repository = Substitute.For<ITrackedTitleRepository>();
        repository.TrackAsync(1, 42, Arg.Any<CancellationToken>())
            .Returns(TrackTitleResult.PinnedCatalogReleaseTitleMismatch);
        var handler = new SetTitleTrackedCommandHandler(repository);

        var result = await handler.HandleAsync(new SetTitleTrackedCommand(1, true, 42));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.PinnedReleaseTitleMismatch");
    }
}
