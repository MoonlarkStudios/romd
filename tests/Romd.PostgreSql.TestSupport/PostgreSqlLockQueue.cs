using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Persistence;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Observes PostgreSQL's lock queue, so a concurrency test never assumes a writer reached a lock by now.</summary>
public static class PostgreSqlLockQueue
{
    /// <summary>
    ///     Starts <paramref name="write" /> on <paramref name="context" />'s own connection and returns once PostgreSQL
    ///     shows that connection waiting for a lock, or once the write finished without waiting. A write that failed
    ///     before it was seen waiting throws its own exception, so the test reports the cause rather than the wait.
    ///     If observation fails, cancels and joins the started write before propagating that failure. On success,
    ///     the caller owns the returned task and its cancellation remains linked to <paramref name="ct" /> until completion.
    /// </summary>
    public static async Task<(Task<T> Pending, bool Waited)> StartWaitingForALockAsync<T>(
        this PostgreSqlTestDatabase database, RomdDbContext context, Func<CancellationToken, Task<T>> write,
        CancellationToken ct)
    {
        int processId = await context.OpenBackendProcessIdAsync(ct);
        return await StartAndObserveAsync(write, (pending, token) => database.WaitsForALockAsync(processId, pending, token), ct);
    }

    internal static async Task<(Task<T> Pending, bool Waited)> StartAndObserveAsync<T>(
        Func<CancellationToken, Task<T>> write, Func<Task, CancellationToken, Task<bool>> observe, CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<T> pending;
        try
        {
            pending = write(cancellation.Token);
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }

        try
        {
            bool waited = await observe(pending, ct);
            return (CompleteAndDisposeAsync(pending, cancellation, ct), waited);
        }
        catch (Exception primaryFailure)
        {
            // Keep cancellation owned here until observation succeeds. Cleanup can report additional
            // failures, but it joins the write first and retains the observation's original cause.
            try
            {
                await CancelAndJoinAsync(cancellation, primaryFailure, pending);
            }
            finally
            {
                cancellation.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    ///     Joins every caller-owned operation without disposing its cancellation source. A caught primary failure must
    ///     be supplied explicitly; unexpected cleanup failures are reported after all joins, retaining that cause.
    /// </summary>
    internal static async Task CancelAndJoinAsync(CancellationTokenSource cancellation, Exception? primaryFailure, params Task[] pending)
    {
        var failures = new List<Exception>();
        CancellationToken ownedToken = default;
        try
        {
            ownedToken = cancellation.Token;
            await cancellation.CancelAsync();
        }
        catch (Exception error)
        {
            Record(error);
        }
        foreach (var operation in pending)
        {
            try
            {
                await operation;
            }
            catch (Exception error)
            {
                Record(error);
            }
        }
        if (failures.Count == 0)
            return;
        if (primaryFailure is not null)
            throw new AggregateException("The operation and its lock queue cleanup failed.", failures.Prepend(primaryFailure));
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Lock queue cleanup failed.", failures);

        void Record(Exception error)
        {
            IEnumerable<Exception> errors = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [error];
            foreach (var failure in errors)
            {
                if (failure is OperationCanceledException cancelled && ownedToken.IsCancellationRequested && cancelled.CancellationToken == ownedToken)
                    continue;
                if (ReferenceEquals(failure, primaryFailure) ||
                    primaryFailure is AggregateException primary && primary.Flatten().InnerExceptions.Any(item => ReferenceEquals(item, failure)))
                    continue;
                if (!failures.Any(item => ReferenceEquals(item, failure)))
                    failures.Add(failure);
            }
        }
    }

    private static async Task<T> CompleteAndDisposeAsync<T>(Task<T> pending, CancellationTokenSource cancellation, CancellationToken callerToken)
    {
        var writeToken = cancellation.Token;
        try
        {
            return await pending;
        }
        catch (OperationCanceledException error) when (writeToken.IsCancellationRequested && error.CancellationToken == writeToken && callerToken.IsCancellationRequested)
        {
            // The caller owns cancellation after a successful observation. Retain that provenance
            // when returning a write that ran with our linked token, and preserve the original cause.
            throw new OperationCanceledException(error.Message, error, callerToken);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>Opens <paramref name="context" />'s connection, kept open until it is disposed, and names its server process.</summary>
    public static async Task<int> OpenBackendProcessIdAsync(this RomdDbContext context, CancellationToken ct)
    {
        await context.Database.OpenConnectionAsync(ct);
        return ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
    }

    /// <summary>
    ///     Returns true once PostgreSQL shows server process <paramref name="processId" /> waiting for a lock, or false once
    ///     <paramref name="pending" />, the write it runs, finished without waiting again. A write already running can be
    ///     observed this way at each of its waits; one that failed before it was seen waiting throws its own exception.
    ///     This overload observes a caller-owned task: if observation fails, its caller must cancel and join the write
    ///     before disposing that write's context or other resources. A task alone supplies no cancellation ownership.
    /// </summary>
    public static async Task<bool> WaitsForALockAsync(
        this PostgreSqlTestDatabase database, int processId, Task pending, CancellationToken ct)
    {
        await using var observer = database.CreateContext();
        while (!pending.IsCompleted)
        {
            if (await observer.Database.SqlQuery<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {processId} AND NOT granted) AS \"Value\"")
                .SingleAsync(ct))
                return true;
            await Task.Delay(10, ct);
        }

        if (!pending.IsCompletedSuccessfully)
            await pending;
        return false;
    }
}
