using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Romd.Persistence;

/// <summary>
///     Classifies save failures that a retry may resolve: connection-level transient faults,
///     serialization failures, and deadlocks. Constraint violations are never transient.
/// </summary>
public static class PostgreSqlTransientErrors
{
    public static bool IsTransient(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException
            {
                SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
            } =>
                true,
            PostgresException => false,
            NpgsqlException { IsTransient: true } => true,
            _ => false
        };
}
