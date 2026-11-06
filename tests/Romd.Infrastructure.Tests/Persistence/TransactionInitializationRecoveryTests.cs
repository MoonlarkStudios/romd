using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TransactionInitializationRecoveryTests
{
    [Fact]
    public async Task BeginTransactionAsync_FenceLostAndDisposalFails_PreservesOwnershipLossAndReleasesTransaction()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        var cleanup = RegisterCleanupFailure(db);
        using var fence = JobExecutionFenceScope.Enter(Guid.NewGuid(), Guid.NewGuid(), TimeProvider.System);

        var actual = await Record.ExceptionAsync(() => new EfUnitOfWork(db).BeginTransactionAsync());

        actual.ShouldBeOfType<JobExecutionOwnershipLostException>();
        actual.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(cleanup);
        db.Database.CurrentTransaction.ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginAsync_UserLockAndDisposalFail_PreservesOriginalFailureAndCancellationToken(bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel
            ? new OperationCanceledException("Interrupted user lock", cancellation.Token)
            : new IOException("Interrupted user lock");
        var interruption = new InterruptUserLock(primary, cancellation, cancel);
        await using var db = new RomdDbContext(database.CreateOptions(options => options
            .ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()
            .AddInterceptors(interruption)));
        var cleanup = RegisterCleanupFailure(db);

        var actual = await Record.ExceptionAsync(() => new PlayActivityUnitOfWork(db).BeginAsync(Guid.NewGuid(), cancellation.Token));

        interruption.Reached.ShouldBeTrue();
        actual.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)actual!).CancellationToken.ShouldBe(cancellation.Token);
        actual!.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(cleanup);
        db.Database.CurrentTransaction.ShouldBeNull();
        (await db.PlaySessions.CountAsync()).ShouldBe(0);
    }

    private static IOException RegisterCleanupFailure(RomdDbContext db)
    {
        var faults = new ProviderTransactionFaults();
        faults.Register(db);
        return faults.CleanupFailure;
    }

    private sealed class InterruptUserLock(Exception primary, CancellationTokenSource cancellation, bool cancel) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                return result;
            Reached = true;
            if (cancel)
                await cancellation.CancelAsync();
            throw primary;
        }
    }

}
