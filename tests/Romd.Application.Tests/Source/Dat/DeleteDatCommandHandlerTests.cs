using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.DeleteDat;
using Romd.Admin.Application.Titles;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class DeleteDatCommandHandlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_PrimaryAndCleanupFail_PreservesCancellationOrOriginalError(bool commit, bool canceled)
    {
        var repository = Substitute.For<IDatRepository>();
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(CreateDat());
        repository.GetCatalogSourceIdAsync(11, Arg.Any<CancellationToken>()).Returns(13);
        var lifecycle = Substitute.For<ISourceLifecycle>();
        lifecycle.GetLinkedTitleIdsAsync(13, Arg.Any<CancellationToken>()).Returns([]);
        lifecycle.GetOrphanedTitleIdsAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>()).Returns([]);
        var handler = new DeleteDatCommandHandler(repository, lifecycle, Substitute.For<ITitleOrphanCleanup>(),
            Substitute.For<ICatalogProjectionService>(), Substitute.For<ILibraryRepository>(), Substitute.For<IAdminEventOutbox>(), work,
            NullLogger<DeleteDatCommandHandler>.Instance);
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary mutation failure");
        transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));
        if (commit)
            transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);
        else
            repository.When(value => value.DeleteAsync(7, Arg.Any<CancellationToken>())).Do(_ => throw primary);

        if (canceled)
        {
            Exception? observed = null;
            try
            { await handler.HandleAsync(DeleteDatCommand.Create(7).Value, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
        }
        else
            (await handler.HandleAsync(DeleteDatCommand.Create(7).Value)).FirstError.Description.ShouldContain(primary.Message);
        await transaction.Received(1).DisposeAsync();
        await transaction.Received(commit ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AcquiresWriterFenceBeforeAffectedTitleSnapshotAndDelete()
    {
        var repository = Substitute.For<IDatRepository>();
        var sourceLifecycle = Substitute.For<ISourceLifecycle>();
        var titleRepository = Substitute.For<ITitleOrphanCleanup>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var libraries = Substitute.For<ILibraryRepository>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(CreateDat());
        repository.GetCatalogSourceIdAsync(11, Arg.Any<CancellationToken>()).Returns(13);
        sourceLifecycle.GetLinkedTitleIdsAsync(13, Arg.Any<CancellationToken>()).Returns([17]);
        sourceLifecycle.GetOrphanedTitleIdsAsync(
                Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var handler = new DeleteDatCommandHandler(
            repository,
            sourceLifecycle,
            titleRepository,
            projection,
            libraries,
            outbox,
            unitOfWork,
            NullLogger<DeleteDatCommandHandler>.Instance);

        var result = await handler.HandleAsync(DeleteDatCommand.Create(7).Value);

        result.IsError.ShouldBeFalse();
        Received.InOrder(() =>
        {
            unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>());
            repository.AcquireMutationWriteLockAsync(7, Arg.Any<CancellationToken>());
            sourceLifecycle.GetLinkedTitleIdsAsync(13, Arg.Any<CancellationToken>());
            repository.DeleteAsync(7, Arg.Any<CancellationToken>());
            sourceLifecycle.GetOrphanedTitleIdsAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 17 })),
                Arg.Any<CancellationToken>());
        });
    }

    private static DatFile CreateDat() => DatFile.Rehydrate(
        7,
        "DAT",
        "DAT",
        null,
        null,
        null,
        DatType.NoIntro,
        null,
        "test.dat",
        1,
        DateTimeOffset.UtcNow,
        null,
        0,
        0,
        0,
        11,
        DatFileLifecycle.Active,
        null);
}
