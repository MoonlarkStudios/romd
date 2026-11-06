namespace Romd.Admin.Application.Common.Persistence;

/// <summary>How the database ended a statement that lost to a concurrent transaction (see <see cref="IUnitOfWork.ConflictOf" />).</summary>
public enum TransactionConflict
{
    /// <summary>The failure is not a lost conflict.</summary>
    None,

    /// <summary>The database aborted the transaction as a deadlock's victim; the other side went on.</summary>
    Deadlock,

    /// <summary>A lock wait outlasted the transaction's lock timeout, and the transaction holding the lock still runs.</summary>
    LockTimeout
}
