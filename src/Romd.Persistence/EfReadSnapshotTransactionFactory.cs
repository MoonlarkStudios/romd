using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Persistence;

/// <summary>
///     PostgreSQL uses RepeatableRead so later
///     statements cannot observe a writer generation committed between bounded read batches.
/// </summary>
public sealed class EfReadSnapshotTransactionFactory(RomdDbContext context)
    : IReadSnapshotTransactionFactory
{
    internal const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<IReadSnapshotTransaction> BeginAsync(CancellationToken cancellationToken = default)
    {
        string providerName = context.Database.ProviderName
            ?? throw new NotSupportedException("A repeatable read snapshot requires a relational database provider.");
        IsolationLevel isolationLevel = GetRequiredIsolationLevel(providerName);

        var transaction = await context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
        return new EfReadSnapshotTransaction(transaction);
    }

    internal static IsolationLevel GetRequiredIsolationLevel(string providerName) =>
        providerName switch
        {
            NpgsqlProviderName => IsolationLevel.RepeatableRead,
            _ => throw new NotSupportedException(
                $"Database provider '{providerName}' has no verified repeatable read snapshot contract.")
        };

    private sealed class EfReadSnapshotTransaction(
        IDbContextTransaction transaction) : IReadSnapshotTransaction
    {
        private bool _completed;
        private bool _disposed;

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_completed)
            {
                return;
            }

            await transaction.CommitAsync(cancellationToken);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (!_completed)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
