using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Persistence;

/// <summary>
///     EF Core implementation of IUnitOfWork.
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly RomdDbContext _context;

    public EfUnitOfWork(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        if (JobExecutionFenceScope.Current is { } fence)
        {
            try
            {
                var now = fence.Clock.GetUtcNow();
                int owned = await _context.Jobs
                    .Where(job => job.Id == fence.JobId && job.ExecutionFenceToken == fence.Token
                        && job.ExecutionLeaseExpiresAtUtc > now && job.CompletedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.UpdatedAt, now), cancellationToken);
                if (owned != 1) throw new JobExecutionOwnershipLostException();
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        return new EfTransaction(transaction, _context);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    private sealed class EfTransaction : ITransaction
    {
        private readonly IDbContextTransaction _transaction;
        private readonly RomdDbContext _context;
        private bool _commitSucceeded;
        private bool _rollbackAttempted;
        private bool _disposed;

        public EfTransaction(IDbContextTransaction transaction, RomdDbContext context)
        {
            _transaction = transaction;
            _context = context;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_commitSucceeded)
            {
                return;
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await _transaction.CommitAsync(cancellationToken);
                _commitSucceeded = true;
            }
            catch
            {
                await RollbackAfterFailedCommitAsync();
                throw;
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_commitSucceeded || _rollbackAttempted)
            {
                return;
            }

            _rollbackAttempted = true;
            try
            {
                await _transaction.RollbackAsync(cancellationToken);
            }
            finally
            {
                _context.ChangeTracker.Clear();
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
                if (!_commitSucceeded && !_rollbackAttempted)
                {
                    await RollbackAsync(CancellationToken.None);
                }
                else if (!_commitSucceeded)
                {
                    _context.ChangeTracker.Clear();
                }
            }
            finally
            {
                await _transaction.DisposeAsync();
            }
        }

        private async Task RollbackAfterFailedCommitAsync()
        {
            if (!_rollbackAttempted)
            {
                _rollbackAttempted = true;
                try
                {
                    await _transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the original commit failure. The transaction is still disposed by
                    // the caller and tracked state is cleared regardless of rollback outcome.
                }
            }

            _context.ChangeTracker.Clear();
        }
    }
}
