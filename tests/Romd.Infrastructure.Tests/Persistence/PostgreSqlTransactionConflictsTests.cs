using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PostgreSqlTransactionConflictsTests
{
    [Fact]
    public void Classify_DeadlockVictim_IsADeadlockHoweverItIsWrapped()
    {
        var deadlock = Postgres(PostgresErrorCodes.DeadlockDetected);

        PostgreSqlTransactionConflicts.Classify(deadlock).ShouldBe(TransactionConflict.Deadlock);
        PostgreSqlTransactionConflicts.Classify(new DbUpdateException("Save failed.", deadlock))
            .ShouldBe(TransactionConflict.Deadlock);
        PostgreSqlTransactionConflicts.Classify(new InvalidOperationException("Outer.", new DbUpdateException("Save failed.", deadlock)))
            .ShouldBe(TransactionConflict.Deadlock);
    }

    [Fact]
    public void Classify_ALockWaitPastTheLockTimeout_IsALockTimeout() =>
        PostgreSqlTransactionConflicts.Classify(new InvalidOperationException("Outer.", Postgres(PostgresErrorCodes.LockNotAvailable)))
            .ShouldBe(TransactionConflict.LockTimeout);

    // Transactions run at Read Committed, so a serialization failure is not a conflict a caller may expect.
    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    public void Classify_OtherDatabaseFailures_AreNoConflict(string sqlState) =>
        PostgreSqlTransactionConflicts.Classify(new DbUpdateException("Save failed.", Postgres(sqlState)))
            .ShouldBe(TransactionConflict.None);

    [Fact]
    public void Classify_AFailureFromOutsideTheDatabase_IsNoConflict() =>
        PostgreSqlTransactionConflicts.Classify(new InvalidOperationException("Not a database failure."))
            .ShouldBe(TransactionConflict.None);

    private static PostgresException Postgres(string sqlState) => new("Failed.", "ERROR", "ERROR", sqlState);
}
