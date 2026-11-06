using Microsoft.EntityFrameworkCore;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PostgreSqlLockQueueTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task StartWaitingForALock_AWriterThatFailsBeforeQueuing_SurfacesItsOwnException()
    {
        await using var context = _database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => _database.StartWaitingForALockAsync<int>(
            context, async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("The writer failed before it reached a lock.");
            }, timeout.Token));

        failure.Message.ShouldBe("The writer failed before it reached a lock.");
    }

    [Fact]
    public async Task WaitsForALock_ObserverCancellation_CallerJoinsItsWriteBeforeContextCleanup()
    {
        using var writeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = _database.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(writeCancellation.Token);
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741238)", writeCancellation.Token);
        await using var writer = _database.CreateContext();
        int processId = await writer.OpenBackendProcessIdAsync(writeCancellation.Token);
        var pending = writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741238)", writeCancellation.Token);
        using var observerCancellation = new CancellationTokenSource();
        Exception? failure = null;
        try
        {
            (await _database.WaitsForALockAsync(processId, pending, writeCancellation.Token)).ShouldBeTrue();
            pending.IsCompleted.ShouldBeFalse();
            await observerCancellation.CancelAsync();

            failure = await Record.ExceptionAsync(() => _database.WaitsForALockAsync(processId, pending, observerCancellation.Token));
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(writeCancellation, failure, pending);
        }

        failure.ShouldBeAssignableTo<OperationCanceledException>();
        pending.IsCompleted.ShouldBeTrue();
        writeCancellation.Token.IsCancellationRequested.ShouldBeTrue();
        // The blocker still holds its lock, but the joined writer has no live command when its context is reused or disposed.
        (await writer.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync()).ShouldBe(1);
    }
}
