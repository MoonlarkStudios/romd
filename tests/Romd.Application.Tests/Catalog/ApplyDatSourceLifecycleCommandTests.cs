using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSourceLifecycle;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class ApplyDatSourceLifecycleCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    [InlineData("disabled")]
    [InlineData("Unknown")]
    public async Task HandleAsync_InvalidAction_RejectsBeforeTransaction(string? action)
    {
        var fixture = new Fixture();

        (await fixture.Handler.HandleAsync(new(7, action!, "review"))).FirstError.Code.ShouldBe("SourceLifecycle.Action");

        fixture.Work.ReceivedCalls().ShouldBeEmpty();
        await fixture.Dats.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_MissingBeforeOrAfterTopologyLock_RejectsBeforeImpactAndMutation(bool disappears)
    {
        var fixture = new Fixture();
        if (disappears)
            fixture.Dats.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(fixture.Dat, (DatFile?)null);
        else
            fixture.Dats.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns((DatFile?)null);

        (await fixture.Handler.HandleAsync(new(7, "Delete", "review"))).FirstError.Code.ShouldBe("SourceLifecycle.NotFound");

        await fixture.Source.DidNotReceiveWithAnyArgs().LockAffectedTitlesAsync(default, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_CatalogOrSourceTryLockRefused_IsBusyBeforeTopologyAndReview(bool catalog)
    {
        var fixture = new Fixture();
        if (catalog)
            fixture.Source.TryLockCatalogAsync("z", Arg.Any<CancellationToken>()).Returns(false);
        else
            fixture.Source.TryLockSourceAsync(11, Arg.Any<CancellationToken>()).Returns(false);

        (await fixture.Handler.HandleAsync(new(7, "Delete", "wrong"))).FirstError.Code.ShouldBe("SourceLifecycle.Busy");

        if (catalog)
            await fixture.Source.DidNotReceiveWithAnyArgs().TryLockSourceAsync(default, default);
        await fixture.Dats.DidNotReceiveWithAnyArgs().AcquireMutationWriteLockAsync(default, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
    }

    [Theory]
    [InlineData(true, "wrong", "SourceLifecycle.Busy")]
    [InlineData(false, "REVIEW", "SourceLifecycle.Stale")]
    [InlineData(false, null, "SourceLifecycle.Stale")]
    public async Task HandleAsync_RevalidatedBusyOrOrdinalTokenMismatch_RejectsBeforeEffects(bool busy, string? review, string code)
    {
        var fixture = new Fixture();
        fixture.Source.ReadImpactAsync(7, 11, "DAT", "Delete", Arg.Any<CancellationToken>()).Returns(fixture.Impact with { Busy = busy });

        (await fixture.Handler.HandleAsync(new(7, "Delete", review!))).FirstError.Code.ShouldBe(code);

        await fixture.Source.Received(1).LockAffectedTitlesAsync(11, Arg.Any<CancellationToken>());
        await fixture.Projection.DidNotReceiveWithAnyArgs().RefreshCatalogSourcePayloadAsync(default, default);
        await fixture.Source.DidNotReceiveWithAnyArgs().BindRelatedSubscriptionsStagedAsync(default, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("Disabled")]
    [InlineData("Discontinued")]
    [InlineData("Active")]
    public async Task HandleAsync_ReviewedAction_PreservesOrderedFencesEffectsAndThreeIntentsBeforeOwnerCommit(string action)
    {
        var fixture = new Fixture();
        fixture.Source.ReadImpactAsync(7, 11, "DAT", action, Arg.Any<CancellationToken>()).Returns(fixture.Impact with { Action = action });

        (await fixture.Handler.HandleAsync(new(7, action, "review"))).IsError.ShouldBeFalse();

        Received.InOrder(() =>
        {
            _ = fixture.Work.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _ = fixture.Dats.GetByIdAsync(7, Arg.Any<CancellationToken>());
            _ = fixture.Source.ReadRelatedCatalogIdsAsync(11, Arg.Any<CancellationToken>());
            _ = fixture.Source.TryLockCatalogAsync("a", Arg.Any<CancellationToken>());
            _ = fixture.Source.TryLockCatalogAsync("z", Arg.Any<CancellationToken>());
            _ = fixture.Source.TryLockSourceAsync(11, Arg.Any<CancellationToken>());
            _ = fixture.Dats.AcquireMutationWriteLockAsync(7, Arg.Any<CancellationToken>());
            _ = fixture.Dats.GetByIdAsync(7, Arg.Any<CancellationToken>());
            _ = fixture.Source.LockAffectedTitlesAsync(11, Arg.Any<CancellationToken>());
            _ = fixture.Source.ReadImpactAsync(7, 11, "DAT", action, Arg.Any<CancellationToken>());
            _ = fixture.Projection.RefreshCatalogSourcePayloadAsync(12, Arg.Any<CancellationToken>());
            _ = fixture.Lifecycle.PreserveOwnedTitleIdentitiesAsync(12, Arg.Any<CancellationToken>());
            _ = fixture.Source.BindRelatedSubscriptionsStagedAsync(11, Arg.Any<CancellationToken>());
            if (action == "Delete")
            {
                _ = fixture.Dats.DeleteAsync(7, Arg.Any<CancellationToken>());
                _ = fixture.Dats.DeleteAsync(9, Arg.Any<CancellationToken>());
                _ = fixture.Dats.DeleteSourceIfOrphanedAsync(11, Arg.Any<CancellationToken>());
                _ = fixture.Orphans.DeleteOrRetainAsync(201, Arg.Any<CancellationToken>());
                _ = fixture.Orphans.DeleteOrRetainAsync(203, Arg.Any<CancellationToken>());
            }
            else
                _ = fixture.Lifecycle.SetStatusAsync(12, Enum.Parse<CatalogSourceStatus>(action), Arg.Any<CancellationToken>());
            _ = fixture.Projection.MarkPlatformDirtyAsync(10, Arg.Any<CancellationToken>(), fixture.Affected);
            _ = fixture.Libraries.FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
            _ = fixture.Outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, Arg.Any<CancellationToken>());
            _ = fixture.Outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, Arg.Any<CancellationToken>());
            _ = fixture.Outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, Arg.Any<CancellationToken>());
            _ = fixture.Transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
        if (action == "Delete")
            await fixture.Lifecycle.DidNotReceiveWithAnyArgs().SetStatusAsync(default, default, default);
        else
            await fixture.Dats.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    private sealed class Fixture
    {
        public IDatRepository Dats { get; } = Substitute.For<IDatRepository>();
        public IDatSourceLifecycleStore Source { get; } = Substitute.For<IDatSourceLifecycleStore>();
        public ISourceLifecycle Lifecycle { get; } = Substitute.For<ISourceLifecycle>();
        public ITitleOrphanCleanup Orphans { get; } = Substitute.For<ITitleOrphanCleanup>();
        public ICatalogProjectionService Projection { get; } = Substitute.For<ICatalogProjectionService>();
        public ILibraryRepository Libraries { get; } = Substitute.For<ILibraryRepository>();
        public IAdminEventOutbox Outbox { get; } = Substitute.For<IAdminEventOutbox>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public DatFile Dat { get; } = DatFile.CreatePendingVersion("DAT", "DAT", DatType.NoIntro, "test.dat", 17, 11, DateTimeOffset.UnixEpoch);
        public SourceRemovalImpact Impact { get; } = new("DAT", "Delete", "review", 2, 1, 1, 1, 1, false);
        public IReadOnlyList<int> Affected { get; } = new[] { 201, 203 };
        public ApplyDatSourceLifecycleCommandHandler Handler => new(Dats, Source, Lifecycle, Orphans, Projection, Libraries, Outbox, Work,
            NullLogger<ApplyDatSourceLifecycleCommandHandler>.Instance);
        public Fixture()
        {
            Dat.Activate();
            Dats.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Dat);
            Dats.GetCatalogSourceIdAsync(11, Arg.Any<CancellationToken>()).Returns(12);
            Dats.GetRoutedPlatformIdsBySourceIdAsync(11, Arg.Any<CancellationToken>()).Returns(new[] { 10 });
            Source.ReadRelatedCatalogIdsAsync(11, Arg.Any<CancellationToken>()).Returns(new[] { "a", "z" });
            Source.TryLockCatalogAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            Source.TryLockSourceAsync(11, Arg.Any<CancellationToken>()).Returns(true);
            Source.ReadImpactAsync(7, 11, "DAT", "Delete", Arg.Any<CancellationToken>()).Returns(Impact);
            Source.ReadVersionIdsAsync(11, Arg.Any<CancellationToken>()).Returns(new[] { 7, 9 });
            Lifecycle.GetLinkedTitleIdsAsync(12, Arg.Any<CancellationToken>()).Returns(Affected);
            Lifecycle.GetOrphanedTitleIdsAsync(Affected, Arg.Any<CancellationToken>()).Returns(Affected);
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
        }
    }
}
