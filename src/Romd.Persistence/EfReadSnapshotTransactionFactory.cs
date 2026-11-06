using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Persistence;

/// <summary>
///     PostgreSQL uses RepeatableRead so later
///     statements cannot observe a writer generation committed between bounded read batches.
/// </summary>
public sealed class EfReadSnapshotTransactionFactory(RomdDbContext context, ILogger<EfReadSnapshotTransactionFactory>? logger = null)
    : IReadSnapshotTransactionFactory
{
    internal const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<IReadSnapshotTransaction> BeginAsync(CancellationToken cancellationToken = default)
    {
        string providerName = context.Database.ProviderName
            ?? throw new NotSupportedException("A repeatable read snapshot requires a relational database provider.");
        IsolationLevel isolationLevel = GetRequiredIsolationLevel(providerName);

        var transaction = await context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
        return new EfReadSnapshotTransaction(transaction, logger ?? NullLogger<EfReadSnapshotTransactionFactory>.Instance);
    }

    internal static IsolationLevel GetRequiredIsolationLevel(string providerName) =>
        providerName switch
        {
            NpgsqlProviderName => IsolationLevel.RepeatableRead,
            _ => throw new NotSupportedException(
                $"Database provider '{providerName}' has no verified repeatable read snapshot contract.")
        };

    private sealed class EfReadSnapshotTransaction(
        IDbContextTransaction transaction, ILogger logger) : IReadSnapshotTransaction
    {
        private bool _completed;
        private bool _disposed;
        private Exception? _completionFailure;

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_completed)
            {
                return;
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
                _completed = true;
            }
            catch (Exception primary)
            {
                _completionFailure = primary;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? primary = _completionFailure;
            try
            {
                if (!_completed)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["Romd.ReadSnapshot.RollbackFailure"] = cleanup;
                logger.LogWarning(cleanup, "Read snapshot rollback failed after completion failed");
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                await TransactionCleanup.DisposeAsync(transaction, _completed, primary, logger);
            }
        }
    }
}
