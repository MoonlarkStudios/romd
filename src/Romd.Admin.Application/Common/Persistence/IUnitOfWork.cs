namespace Romd.Admin.Application.Common.Persistence;

/// <summary>
///     Port for managing database transactions.
///     Allows application services to coordinate transactional operations
///     without depending on specific ORM implementations.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    ///     Flushes pending persistence changes without committing the active transaction.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Begins a new database transaction.
    /// </summary>
    Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Represents an active database transaction. Disposal rolls back uncommitted changes
///     using a non-cancelled token and clears staged state, including when the caller returns
///     a business error before commit.
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>
    ///     Flushes pending unit-of-work changes and commits the transaction.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rolls back all changes made within this transaction.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
