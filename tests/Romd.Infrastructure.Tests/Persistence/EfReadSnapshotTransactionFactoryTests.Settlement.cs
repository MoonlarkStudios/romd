using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class EfReadSnapshotTransactionFactoryTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task Snapshot_RealCompletionAndCleanupFaults_PreserveAcknowledgedReadOrOriginalFailure(string phase, bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedSettlementPlatformAsync(database);
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>()));
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original snapshot completion cancellation", cancellation.Token)
            : new IOException("Original snapshot completion failure");
        var faults = new CatalogTransactionFaults(phase, primary, cancel ? cancellation.Cancel : null, failRollback: true);
        faults.Register(db);
        var logger = Substitute.For<ILogger<EfReadSnapshotTransactionFactory>>();
        var snapshot = await new EfReadSnapshotTransactionFactory(db, logger).BeginAsync(cancellation.Token);

        var observed = await Record.ExceptionAsync(async () =>
        {
            await using var owned = snapshot;
            db.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel.ShouldBe(IsolationLevel.RepeatableRead);
            (await db.Platforms.SingleAsync(cancellation.Token)).Name.ShouldBe("Before");
            await using (var writer = database.CreateContext())
                (await writer.Platforms.ExecuteUpdateAsync(update => update.SetProperty(row => row.Name, "After"))).ShouldBe(1);
            (await db.Platforms.SingleAsync(cancellation.Token)).Name.ShouldBe("Before");
            await owned.CompleteAsync(cancellation.Token);
        });

        if (phase == "none")
            observed.ShouldBeNull();
        else
        {
            observed.ShouldBeSameAs(primary);
            if (cancel)
                ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
            primary.Data["Romd.ReadSnapshot.RollbackFailure"].ShouldBeSameAs(faults.RollbackFailure);
            primary.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        }
        await snapshot.DisposeAsync();
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(phase is "none" or "after" ? 1 : 0);
        faults.RollbackAttempts.ShouldBe(phase == "none" ? 0 : 1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        logger.ReceivedCalls().ShouldContain(call => call.GetArguments().Contains(faults.CleanupFailure));
        (await db.Platforms.SingleAsync()).Name.ShouldBe("After");
    }

    [Fact]
    public async Task Snapshot_IncompleteReadAndBothCleanupFaults_ReportsRollbackCauseAndStillReleasesProvider()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedSettlementPlatformAsync(database);
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>()));
        var faults = new CatalogTransactionFaults(failRollback: true);
        faults.Register(db);
        var snapshot = await new EfReadSnapshotTransactionFactory(db).BeginAsync();
        (await db.Platforms.SingleAsync()).Name.ShouldBe("Before");

        var observed = await Record.ExceptionAsync(async () => await snapshot.DisposeAsync());

        observed.ShouldBeSameAs(faults.RollbackFailure);
        faults.RollbackFailure.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        await snapshot.DisposeAsync();
        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        (await db.Platforms.SingleAsync()).Name.ShouldBe("Before");
    }

    private static async Task SeedSettlementPlatformAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Before", ShortName = "before" });
        await db.SaveChangesAsync();
    }
}
