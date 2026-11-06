using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>Faults a single context's real provider transaction at acknowledgement and disposal boundaries.</summary>
internal sealed class ProviderTransactionFaults(string commitFault = "none", Exception? primary = null, Action? beforeFault = null)
{
    private readonly string _commitFault = commitFault;
    public IOException CleanupFailure { get; } = new("Secondary provider transaction disposal failure");
    public int Commits { get; private set; }
    public int Disposals { get; private set; }
    public void Register(DbContext context) => ((Factory)context.GetService<IRelationalTransactionFactory>()).Register(context, this);

    private void Fail()
    {
        beforeFault?.Invoke();
        throw primary ?? new InvalidOperationException("No primary commit failure configured");
    }

    internal sealed class Factory(RelationalTransactionFactoryDependencies dependencies) : RelationalTransactionFactory(dependencies)
    {
        private readonly ConditionalWeakTable<DbContext, ProviderTransactionFaults> _faults = new();
        public void Register(DbContext context, ProviderTransactionFaults faults) => _faults.Add(context, faults);
        public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction,
            Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned) =>
            connection.Context is { } context && _faults.TryGetValue(context, out var faults)
                ? new Transaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper, faults)
                : base.Create(connection, transaction, transactionId, logger, transactionOwned);
    }

    private sealed class Transaction(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper, ProviderTransactionFaults faults)
        : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (faults._commitFault == "before")
                faults.Fail();
            await base.CommitAsync(cancellationToken);
            faults.Commits++;
            if (faults._commitFault == "after")
                faults.Fail();
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            faults.Disposals++;
            throw faults.CleanupFailure;
        }
    }
}
