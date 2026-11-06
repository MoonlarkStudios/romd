using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class TitleMediaReadConflictTests
{
    private readonly ITitleAggregateReader _titles = Substitute.For<ITitleAggregateReader>();
    private readonly ITitleMediaStore _media = Substitute.For<ITitleMediaStore>();
    private readonly IUnitOfWork _unit = Substitute.For<IUnitOfWork>();
    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();
    private readonly IArtworkCurationRepository _artwork = Substitute.For<IArtworkCurationRepository>();
    private readonly IAdminEventOutbox _outbox = Substitute.For<IAdminEventOutbox>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_InitialSnapshotChurn_ReturnsReadConflictWithoutStartingMutation(bool delete)
    {
        _titles.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromException<Title?>(
            new PersistenceConflictException(new InvalidOperationException("private read failure"))));

        var error = await Execute(delete, default);

        error.Type.ShouldBe(ErrorType.Conflict);
        error.Code.ShouldBe("Titles.ReadConflict");
        _unit.ReceivedCalls().ShouldBeEmpty();
        _media.ReceivedCalls().ShouldBeEmpty();
        _files.ReceivedCalls().ShouldBeEmpty();
        _artwork.ReceivedCalls().ShouldBeEmpty();
        _outbox.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CancellationRacesSnapshotChurn_PreservesCancellation(bool delete)
    {
        using var cancellation = new CancellationTokenSource();
        _titles.GetAggregateAsync(7, cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromException<Title?>(new PersistenceConflictException(new InvalidOperationException()));
        });

        var error = await Should.ThrowAsync<OperationCanceledException>(() => Execute(delete, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        _unit.ReceivedCalls().ShouldBeEmpty();
        _files.ReceivedCalls().ShouldBeEmpty();
    }

    private async Task<Error> Execute(bool delete, CancellationToken ct)
    {
        var precondition = RevisionPrecondition.For(Guid.NewGuid().ToString());
        var detail = Substitute.For<ITitleDetailReader>();
        if (delete)
            return (await new DeleteTitleMediaCommandHandler(TestSystemCatalog.Create(), _titles, detail,
                _media, _artwork, _files, _outbox, _unit, Options.Create(new EnrichmentOptions()),
                NullLogger<DeleteTitleMediaCommandHandler>.Instance).HandleAsync(new(7, 1, precondition), ct)).FirstError;
        return (await new SetPrimaryMediaCommandHandler(TestSystemCatalog.Create(), _titles, _media, detail,
            _unit, NullLogger<SetPrimaryMediaCommandHandler>.Instance).HandleAsync(new(7, 1, precondition), ct)).FirstError;
    }
}
