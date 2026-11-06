using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Observes real catalog transactions and injects settlement faults for one context only.</summary>
internal sealed class CatalogTransactionFaults(string commitFault = "none", Exception? primary = null,
    Action? beforeFault = null, bool failRollback = false, bool failSavepointRollback = false,
    bool failBeforeSavepointRollback = false, bool failDisposal = true, bool failSavepointRelease = false,
    string? savepointName = null)
{
    private readonly string _commitFault = commitFault;
    private readonly bool _failRollback = failRollback;
    private readonly bool _failSavepointRollback = failSavepointRollback;
    private readonly bool _failBeforeSavepointRollback = failBeforeSavepointRollback;
    private readonly bool _failDisposal = failDisposal;
    private readonly bool _failSavepointRelease = failSavepointRelease;
    private readonly string? _savepointName = savepointName;
    public IOException CleanupFailure { get; } = new("Secondary catalog transaction disposal failure");
    public IOException RollbackFailure { get; } = new("Secondary catalog transaction rollback failure");
    public IOException SavepointFailure { get; } = new("Secondary catalog savepoint rollback failure");
    public IOException SavepointReleaseFailure { get; } = new("Secondary catalog savepoint release failure");
    public int CommitAttempts { get; private set; }
    public int ProviderCommits { get; private set; }
    public int RollbackAttempts { get; private set; }
    public int SavepointRollbackAttempts { get; private set; }
    public int SavepointReleaseAttempts { get; private set; }
    public int Disposals { get; private set; }
    public void Register(DbContext context) => ((Factory)context.GetService<IRelationalTransactionFactory>()).Register(context, this);

    private void FailCommit()
    {
        beforeFault?.Invoke();
        throw primary ?? new InvalidOperationException("A commit fault requires its primary exception.");
    }

    internal sealed class Factory(RelationalTransactionFactoryDependencies dependencies) : RelationalTransactionFactory(dependencies)
    {
        private readonly ConditionalWeakTable<DbContext, CatalogTransactionFaults> _faults = new();
        public void Register(DbContext context, CatalogTransactionFaults faults) => _faults.Add(context, faults);
        public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction,
            Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned) =>
            connection.Context is { } context && _faults.TryGetValue(context, out var faults)
                ? new Transaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper, faults)
                : base.Create(connection, transaction, transactionId, logger, transactionOwned);
    }

    private sealed class Transaction(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper, CatalogTransactionFaults faults)
        : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            faults.CommitAttempts++;
            if (faults._commitFault == "before")
                faults.FailCommit();
            await base.CommitAsync(cancellationToken);
            faults.ProviderCommits++;
            if (faults._commitFault == "after")
                faults.FailCommit();
        }

        public override async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            faults.RollbackAttempts++;
            try
            { await base.RollbackAsync(cancellationToken); }
            // A lost acknowledgement may already have completed the provider transaction.
            catch (Exception) when (faults._failRollback) { }
            if (faults._failRollback)
                throw faults.RollbackFailure;
        }

        public override async Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            if (faults._savepointName is not null && name != faults._savepointName)
            {
                await base.RollbackToSavepointAsync(name, cancellationToken);
                return;
            }
            faults.SavepointRollbackAttempts++;
            if (faults._failBeforeSavepointRollback)
                throw faults.SavepointFailure;
            await base.RollbackToSavepointAsync(name, cancellationToken);
            if (faults._failSavepointRollback)
                throw faults.SavepointFailure;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            faults.Disposals++;
            if (faults._failDisposal)
                throw faults.CleanupFailure;
        }

        public override async Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            if (faults._savepointName is not null && name != faults._savepointName)
            {
                await base.ReleaseSavepointAsync(name, cancellationToken);
                return;
            }
            faults.SavepointReleaseAttempts++;
            await base.ReleaseSavepointAsync(name, cancellationToken);
            if (faults._failSavepointRelease)
                throw faults.SavepointReleaseFailure;
        }
    }
}
