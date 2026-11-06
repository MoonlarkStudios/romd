using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed partial class CatalogProjectionServiceTests
{
    [Fact]
    public async Task Rebuild_AcknowledgedCommitAndDisposalFailure_ReturnsDurableCleanGraph()
    {
        await SeedSettlementGraphAsync();
        await using var db = SettlementContext();
        var faults = new CatalogTransactionFaults();
        faults.Register(db);

        (await CreateService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();

        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(1);
        faults.SavepointRollbackAttempts.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertSettlementGraphAsync(2, CatalogRebuildState.Clean);
    }

    [Theory]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task Rebuild_CommitFailureAndDisposalFailure_PreservesPrimaryWithoutSavepointRecoveryOrSecondCommit(string commitFault, bool cancelled)
    {
        await SeedSettlementGraphAsync();
        await using var db = SettlementContext();
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original catalog commit cancellation", cancellation.Token)
            : new IOException("Original catalog commit failure");
        var faults = new CatalogTransactionFaults(commitFault, primary, cancelled ? cancellation.Cancel : null);
        faults.Register(db);

        var observed = await Record.ExceptionAsync(() => CreateService(db).RebuildPlatformAsync(PlatformId, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancelled)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.CatalogProjection.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        primary.Data.Contains("Romd.CatalogProjection.RecoveryFailure").ShouldBeFalse();
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(commitFault == "after" ? 1 : 0);
        faults.SavepointRollbackAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertSettlementGraphAsync(commitFault == "after" ? 2 : 1, CatalogRebuildState.Clean);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("io")]
    [InlineData("deadlock")]
    public async Task Rebuild_PreCommitFailureAndDisposalFailure_RetainsOriginalSavepointRecoveryPolicy(string failure)
    {
        await SeedSettlementGraphAsync();
        await using var db = SettlementContext();
        var faults = new CatalogTransactionFaults();
        faults.Register(db);
        using var cancellation = new CancellationTokenSource();
        Exception primary = SettlementPrimary(failure, cancellation.Token);
        var service = ServiceFailingAfterPayloadRefresh(db, primary, failure == "cancel" ? cancellation.Cancel : null);

        if (failure == "cancel")
        {
            var observed = await Record.ExceptionAsync(() => service.RebuildPlatformAsync(PlatformId, cancellation.Token));
            observed.ShouldBeSameAs(primary);
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await service.RebuildPlatformAsync(PlatformId)).ShouldBeFalse();

        primary.Data["Romd.CatalogProjection.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(1);
        faults.SavepointRollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertSettlementGraphAsync(1, failure == "io" ? CatalogRebuildState.Failed : CatalogRebuildState.Dirty,
            failure == "io" ? primary.Message : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebuild_PrimaryAndSavepointRollbackAndDisposalFail_PreservesOriginalCauseAndRollsBackAllChanges(bool cancelled)
    {
        await SeedSettlementGraphAsync();
        await using var db = SettlementContext();
        var faults = new CatalogTransactionFaults(failSavepointRollback: true);
        faults.Register(db);
        using var cancellation = new CancellationTokenSource();
        Exception primary = SettlementPrimary(cancelled ? "cancel" : "io", cancellation.Token);
        var service = ServiceFailingAfterPayloadRefresh(db, primary, cancelled ? cancellation.Cancel : null);

        var observed = await Record.ExceptionAsync(() => service.RebuildPlatformAsync(PlatformId, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancelled)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.CatalogProjection.RecoveryFailure"].ShouldBeSameAs(faults.SavepointFailure);
        primary.Data["Romd.CatalogProjection.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(0);
        faults.SavepointRollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertSettlementGraphAsync(1, CatalogRebuildState.Clean);
    }

    [Theory]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task Rebuild_RecoveryCommitAndDisposalFail_PreservesWorkFailureAndDoesNotRepeatSettlement(string commitFault, bool cancelled)
    {
        await SeedSettlementGraphAsync();
        await using var db = SettlementContext();
        using var cancellation = new CancellationTokenSource();
        Exception primary = SettlementPrimary(cancelled ? "cancel" : "io", cancellation.Token);
        var recovery = new IOException("Secondary recovery commit acknowledgement failure");
        var faults = new CatalogTransactionFaults(commitFault, recovery);
        faults.Register(db);
        var service = ServiceFailingAfterPayloadRefresh(db, primary, cancelled ? cancellation.Cancel : null);

        var observed = await Record.ExceptionAsync(() => service.RebuildPlatformAsync(PlatformId, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancelled)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.CatalogProjection.RecoveryFailure"].ShouldBeSameAs(recovery);
        primary.Data["Romd.CatalogProjection.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(commitFault == "after" ? 1 : 0);
        faults.SavepointRollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        var expected = commitFault == "before" ? CatalogRebuildState.Clean
            : cancelled ? CatalogRebuildState.Dirty : CatalogRebuildState.Failed;
        await AssertSettlementGraphAsync(1, expected, expected == CatalogRebuildState.Failed ? primary.Message : null);
    }

    private RomdDbContext SettlementContext() => new(_connection.CreateOptions(options =>
        options.ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>()));

    private async Task SeedSettlementGraphAsync()
    {
        await using var seed = CreateDb();
        SeedTitle(seed, 10);
        SeedMappedGame(seed, 1, 1, 1, 10, NewSha1(0x11));
        await seed.SaveChangesAsync();
        (await CreateService(seed).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
        SeedMappedGame(seed, 1, 2, 2, 10, NewSha1(0x22));
        await seed.SaveChangesAsync();
    }

    private static Exception SettlementPrimary(string failure, CancellationToken ct) => failure switch
    {
        "cancel" => new OperationCanceledException("Original catalog projection cancellation", ct),
        "deadlock" => new PostgresException("Original catalog projection deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected),
        _ => new IOException("Original catalog projection failure")
    };

    private static CatalogProjectionService ServiceFailingAfterPayloadRefresh(RomdDbContext db, Exception primary, Action? beforeFailure)
    {
        var actual = new TitlePayloadAvailabilityProjection(db,
            new CatalogPayloadAssertionReader(db, [new DatCatalogPayloadAssertionProvider(db)]));
        var payload = Substitute.For<ITitlePayloadAvailabilityProjection>();
        payload.RefreshPlatformPayloadAssertionsAsync(PlatformId, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await actual.RefreshPlatformPayloadAssertionsAsync(PlatformId, call.Arg<CancellationToken>());
            // The new graph and its payload projection have reached PostgreSQL before recovery is exercised.
            (await db.CatalogReleases.CountAsync()).ShouldBe(2);
            beforeFailure?.Invoke();
            throw primary;
        });
        return new CatalogProjectionService(db,
            new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(db)]), payload, new DiscSetService(db),
            new EfUnitOfWork(db), TimeProvider.System, NullLogger<CatalogProjectionService>.Instance);
    }

    private async Task AssertSettlementGraphAsync(int expectedReleases, CatalogRebuildState expectedState, string? expectedError = null)
    {
        await using var verify = CreateDb();
        (await verify.CatalogReleases.CountAsync()).ShouldBe(expectedReleases);
        (await verify.CatalogReleaseFiles.CountAsync()).ShouldBe(expectedReleases);
        (await verify.CatalogReleaseSources.CountAsync()).ShouldBe(expectedReleases);
        (await verify.CatalogReleaseFileSources.CountAsync()).ShouldBe(expectedReleases);
        var platform = await verify.Platforms.SingleAsync();
        platform.CatalogRebuildState.ShouldBe(expectedState);
        platform.CatalogRebuildError.ShouldBe(expectedError);
        // Dirty/Failed retain the timestamp of the previous successful graph.
        platform.CatalogRebuiltAt.ShouldNotBeNull();
        if (expectedState == CatalogRebuildState.Failed)
            platform.CatalogRebuildFailedAtUtc.ShouldNotBeNull();
        else
            platform.CatalogRebuildFailedAtUtc.ShouldBeNull();
    }
}
