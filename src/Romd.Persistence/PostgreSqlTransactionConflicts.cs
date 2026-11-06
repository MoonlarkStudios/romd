using Npgsql;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Persistence;

/// <summary>
///     Classifies a failure in which PostgreSQL ended a statement because it lost to a concurrent transaction, walking
///     inner exceptions. It is separate from <see cref="PostgreSqlTransientErrors" />, whose faults an idempotent save may
///     simply repeat: these leave the caller to decide whether its own work may run again.
/// </summary>
public static class PostgreSqlTransactionConflicts
{
    public static TransactionConflict Classify(Exception? error) => error switch
    {
        null => TransactionConflict.None,
        PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } => TransactionConflict.Deadlock,
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } => TransactionConflict.LockTimeout,
        _ => Classify(error.InnerException)
    };
}
