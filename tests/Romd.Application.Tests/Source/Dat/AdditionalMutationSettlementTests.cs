using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class AdditionalMutationSettlementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Activate_PrimaryAndCleanupFail_PreservesCancellationOrOriginalError(bool commit, bool canceled)
    {
        var rig = new Activation();
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary activation failure");
        rig.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));
        if (commit)
            rig.Transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);
        else
            rig.Repository.When(value => value.UpdateLifecycleStagedAsync(Arg.Any<DatFile>(), Arg.Any<CancellationToken>())).Do(_ => throw primary);

        if (canceled)
        {
            Exception? observed = null;
            try
            { await rig.Handler.HandleAsync(ActivateDatVersionCommand.Create(7).Value, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
        }
        else
            (await rig.Handler.HandleAsync(ActivateDatVersionCommand.Create(7).Value)).FirstError.Description.ShouldContain(primary.Message);
        await rig.Transaction.Received(1).DisposeAsync();
        await rig.Transaction.Received(commit ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Activate_AcknowledgedCommitAndCleanupFail_ReturnsActiveVersion()
    {
        var rig = new Activation();
        rig.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));

        var result = await rig.Handler.HandleAsync(ActivateDatVersionCommand.Create(7).Value);

        result.Value.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        await rig.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await rig.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Activate_ActiveReplayUnderLock_KeepsConvergedIdentityWithoutFlushingOrCommitting()
    {
        var rig = new Activation();
        rig.Repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Version(DatFileLifecycle.PendingActivation), Version(DatFileLifecycle.Active));
        rig.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("uncommitted replay cleanup")));

        var result = await rig.Handler.HandleAsync(ActivateDatVersionCommand.Create(7).Value);

        result.Value.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        await rig.Transaction.DidNotReceiveWithAnyArgs().CommitAsync();
        await rig.Work.DidNotReceiveWithAnyArgs().FlushAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Purge_PrimaryAndCleanupFail_PreservesExactFailureAndDoesNotPrune(bool commit, bool canceled)
    {
        var rig = new Purge();
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary purge failure");
        rig.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));
        if (commit)
            rig.Transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);
        else
            rig.Roms.DeleteUnidentifiedAsync(Arg.Any<CancellationToken>()).Returns<int>(_ => throw primary);

        Exception? observed = null;
        try
        { await rig.Handler.HandleAsync(new(), cancellation.Token); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        await rig.Storage.DidNotReceiveWithAnyArgs().PruneUnreferencedFilesAsync(default, default);
        await rig.Transaction.Received(1).DisposeAsync();
        await rig.Transaction.Received(commit ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Purge_AcknowledgedDeletionAndPruneFailure_ReturnsDurableDeletionWithoutInventingReclaimedCount(bool canceled)
    {
        var rig = new Purge();
        Exception cleanup = canceled ? new OperationCanceledException() : new IOException("prune interrupted");
        rig.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("transaction cleanup")));
        rig.Storage.PruneUnreferencedFilesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns<int>(_ => throw cleanup);

        var result = await rig.Handler.HandleAsync(new());

        result.Value.DeletedCount.ShouldBe(2);
        result.Value.ReclaimedFileCount.ShouldBe(0);
        await rig.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await rig.Transaction.Received(1).DisposeAsync();
        await rig.Storage.Received(1).PruneUnreferencedFilesAsync(TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_NoDeletion_DoesNotCommitAndRetainsPruneFailure()
    {
        var rig = new Purge();
        var primary = new IOException("prune is the only requested change");
        rig.Roms.DeleteUnidentifiedAsync(Arg.Any<CancellationToken>()).Returns(0);
        rig.Storage.PruneUnreferencedFilesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns<int>(_ => throw primary);

        (await Should.ThrowAsync<IOException>(() => rig.Handler.HandleAsync(new()))).ShouldBeSameAs(primary);

        await rig.Work.DidNotReceiveWithAnyArgs().FlushAsync();
        await rig.Transaction.DidNotReceiveWithAnyArgs().CommitAsync();
        await rig.Transaction.Received(1).DisposeAsync();
    }

    private sealed class Activation
    {
        public IDatRepository Repository { get; } = Substitute.For<IDatRepository>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public ActivateDatVersionCommandHandler Handler { get; }
        public Activation()
        {
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(_ => Version(DatFileLifecycle.PendingActivation));
            Repository.GetRoutedPlatformIdsBySourceIdAsync(11, Arg.Any<CancellationToken>()).Returns([]);
            var source = Substitute.For<ISourceLifecycle>();
            source.GetLinkedTitleIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Handler = new(Repository, Substitute.For<ICatalogProjectionService>(), Substitute.For<ILibraryRepository>(),
                Substitute.For<IAdminEventOutbox>(), Work, TimeProvider.System, NullLogger<ActivateDatVersionCommandHandler>.Instance, source);
        }
    }

    private sealed class Purge
    {
        public IRomRepository Roms { get; } = Substitute.For<IRomRepository>();
        public IFileStorageService Storage { get; } = Substitute.For<IFileStorageService>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public PurgeUnidentifiedRomsCommandHandler Handler { get; }
        public Purge()
        {
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Roms.DeleteUnidentifiedAsync(Arg.Any<CancellationToken>()).Returns(2);
            Handler = new(Roms, Storage, Substitute.For<IAdminEventOutbox>(), Work, NullLogger<PurgeUnidentifiedRomsCommandHandler>.Instance);
        }
    }

    private static DatFile Version(DatFileLifecycle lifecycle) => DatFile.Rehydrate(
        7, "DAT", "DAT", null, null, null, DatType.NoIntro, null, "test.dat", 1,
        DateTimeOffset.UnixEpoch, null, 0, 0, 0, 11, lifecycle, null);
}
