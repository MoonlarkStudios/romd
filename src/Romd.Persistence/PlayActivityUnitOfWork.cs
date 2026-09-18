using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Consumer.Application.Activity;

namespace Romd.Persistence;

public sealed class PlayActivityUnitOfWork(RomdDbContext context) : IPlayActivityUnitOfWork
{
    public async Task<IPlayActivityTransaction> BeginAsync(Guid userId, CancellationToken ct = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // The composite database key is per user. Serializing one user's upserts also makes two
            // simultaneous first deliveries of the same client-generated session ID idempotent.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))",
                ct);
            return new Transaction(transaction, context);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class Transaction(IDbContextTransaction transaction, RomdDbContext context)
        : IPlayActivityTransaction
    {
        private bool _committed;
        private bool _rollbackAttempted;
        private bool _disposed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (_committed)
            {
                return;
            }

            try
            {
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                _committed = true;
            }
            catch
            {
                await RollbackAfterFailedCommitAsync();
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
            try
            {
                if (!_committed && !_rollbackAttempted)
                {
                    await RollbackAsync();
                }
                else if (!_committed)
                {
                    context.ChangeTracker.Clear();
                }
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }

        private async Task RollbackAsync()
        {
            _rollbackAttempted = true;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }

        private async Task RollbackAfterFailedCommitAsync()
        {
            _rollbackAttempted = true;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the commit failure; disposal still releases the provider transaction.
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }
    }
}
