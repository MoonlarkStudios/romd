using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed partial class ReferenceAssetLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_PrimaryAndTransactionDisposalFail_PreservesOriginalCauseAndRollsBack(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var interruption = new InterruptReferenceSave(false, cancel ? cancellation : null);
        await using var db = new RomdDbContext(_database.CreateOptions(options => options
            .ReplaceService<IRelationalTransactionFactory, FailReferenceTransactionDisposalFactory>()
            .AddInterceptors(interruption)));
        var disposal = new ReferenceTransactionDisposalFailure();
        db.GetService<IRelationalTransactionFactory>().ShouldBeOfType<FailReferenceTransactionDisposalFactory>().Register(db, disposal);
        var cas = Store();
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)));

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db,
            observer, TimeProvider.System, cancellation.Token));

        actual.ShouldBeSameAs(interruption.Primary);
        if (cancel)
            actual.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        actual!.Data["Romd.ReferenceCatalog.TransactionDisposalFailure"].ShouldBeSameAs(disposal.Failure);
        interruption.Reached.ShouldBeTrue();
        disposal.Attempts.ShouldBe(1);
        observer.DisposeCount.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        await AssertEmptyBootstrapAsync(verify);
    }

    [Fact]
    public async Task Bootstrap_OnlyTransactionDisposalFails_PropagatesFailureAndRetainsCommittedCatalog()
    {
        await using var db = new RomdDbContext(_database.CreateOptions(options => options
            .ReplaceService<IRelationalTransactionFactory, FailReferenceTransactionDisposalFactory>()));
        var disposal = new ReferenceTransactionDisposalFailure();
        db.GetService<IRelationalTransactionFactory>().ShouldBeOfType<FailReferenceTransactionDisposalFactory>().Register(db, disposal);
        var cas = Store();
        var observer = new ObserveReferenceBridge(new ReferenceBlobStore(cas, Ledger(db, cas)));

        var actual = await Record.ExceptionAsync(() => SharedReferenceDataSeeder.InitializeAsync(db,
            observer, TimeProvider.System));

        actual.ShouldBeSameAs(disposal.Failure);
        disposal.Attempts.ShouldBe(1);
        observer.DisposeCount.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var verify = _database.CreateContext();
        (await verify.ReferenceCatalogState.CountAsync()).ShouldBe(1);
        (await verify.ReferenceAssets.CountAsync()).ShouldBeGreaterThan(0);
        (await verify.Files.CountAsync()).ShouldBeGreaterThan(0);
        (await verify.Platforms.CountAsync()).ShouldBeGreaterThan(0);
        (await verify.Ratings.CountAsync()).ShouldBeGreaterThan(0);
    }

    private sealed class ReferenceTransactionDisposalFailure
    {
        public IOException Failure { get; } = new("Bootstrap transaction disposal failed.");
        public int Attempts { get; set; }
    }

    // Keep the real provider transaction: fault only after EF has disposed it and released its connection.
    // The factory is shared by EF contexts, so injection is keyed to this test's context, never global state.
    private sealed class FailReferenceTransactionDisposalFactory : RelationalTransactionFactory
    {
        private readonly ConditionalWeakTable<DbContext, ReferenceTransactionDisposalFailure> _failures = new();

        public FailReferenceTransactionDisposalFactory(RelationalTransactionFactoryDependencies dependencies) : base(dependencies) { }

        public void Register(DbContext context, ReferenceTransactionDisposalFailure failure) => _failures.Add(context, failure);

        public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction,
            Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned)
            => connection.Context is { } context && _failures.TryGetValue(context, out var failure)
                ? new FailReferenceTransactionDisposal(connection, transaction, transactionId, logger, transactionOwned,
                    Dependencies.SqlGenerationHelper, failure)
                : base.Create(connection, transaction, transactionId, logger, transactionOwned);
    }

    private sealed class FailReferenceTransactionDisposal(IRelationalConnection connection, DbTransaction transaction,
        Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper, ReferenceTransactionDisposalFailure failure)
        : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        public override async ValueTask DisposeAsync()
        {
            failure.Attempts++;
            await base.DisposeAsync();
            throw failure.Failure;
        }
    }
}
