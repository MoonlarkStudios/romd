using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Enrichment;

/// <summary>Bounded observations of this synthetic test database; service connections and queries stay unchanged.</summary>
internal sealed class ScaleQueryDiagnostics : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ITestOutputHelper _output;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<(string Sql, NpgsqlParameter[] Parameters)> _count = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _monitor;
    private readonly Task _plan;
    private int _captured;

    public ScaleQueryDiagnostics(string connectionString, ITestOutputHelper output)
    {
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false, Timeout = 2, CommandTimeout = 2, ApplicationName = "scale-diagnostics" }.ConnectionString;
        _output = output;
        _monitor = MonitorAsync();
        _plan = PlanAsync();
    }

    public ObservedCommands Reader(int reader) => new(this, reader);

    private void Write(string message)
    {
        try
        { _output.WriteLine(message); }
        catch (Exception) { } // A diagnostic sink must never change the service command or its exception.
    }

    private async Task MonitorAsync()
    {
        try
        {
            for (int sample = 0; sample < 60; sample++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
                await SnapshotAsync($"sample-{sample}");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task SnapshotAsync(string label)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pid, coalesce(state, '-'), coalesce(wait_event_type, '-'), coalesce(wait_event, '-'),
                       coalesce(extract(epoch FROM clock_timestamp() - query_start)::text, '-'),
                       coalesce(array_to_string(pg_blocking_pids(pid), ','), '')
                FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                ORDER BY pid;
                SELECT temp_files, temp_bytes FROM pg_stat_database WHERE datname = current_database();
                """;
            await using var rows = await command.ExecuteReaderAsync(deadline.Token);
            while (await rows.ReadAsync(deadline.Token))
                Write($"DB[{label}] pid={rows.GetInt32(0)}, state={rows.GetString(1)}, " +
                    $"wait={rows.GetString(2)}/{rows.GetString(3)}, age={rows.GetString(4)}, blockers={rows.GetString(5)}");
            if (await rows.NextResultAsync(deadline.Token) && await rows.ReadAsync(deadline.Token))
                Write($"DB[{label}] temp_files={rows.GetInt64(0)}, temp_bytes={rows.GetInt64(1)}");
        }
        catch (Exception error)
        {
            // Observations must not replace the original assertion or database exception.
            Write($"DB[{label}] diagnostic unavailable: {error.GetType().Name}");
        }
    }

    private void CaptureCount(DbCommand command)
    {
        if (Interlocked.CompareExchange(ref _captured, 1, 0) != 0)
            return;
        var parameters = command.Parameters.Cast<NpgsqlParameter>().Select(parameter =>
        {
            var copy = (NpgsqlParameter)((ICloneable)parameter).Clone();
            if (copy.Value is Array array)
                copy.Value = array.Clone();
            return copy;
        }).ToArray();
        Write("Exact count SQL: " + command.CommandText);
        foreach (var parameter in parameters)
            Write($"Count parameter {parameter.ParameterName}: {parameter.NpgsqlDbType}, " +
                (parameter.Value is Array array ? $"array[{array.Length}]" : parameter.Value?.GetType().Name ?? "null"));
        _count.TrySetResult((command.CommandText, parameters));
    }

    private async Task PlanAsync()
    {
        try
        {
            var count = await _count.Task.WaitAsync(_stop.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            // No ANALYZE: observe planning without executing/warming the service query.
            command.CommandText = "EXPLAIN (FORMAT JSON, SETTINGS) " + count.Sql;
            command.Parameters.AddRange(count.Parameters);
            string plan = (string)(await command.ExecuteScalarAsync(deadline.Token))!;
            Write("Exact count EXPLAIN (separate diagnostic session): " + plan[..Math.Min(plan.Length, 131072)]);
            command.Parameters.Clear();
            command.CommandText = "SELECT name, setting FROM pg_settings WHERE name IN ('work_mem', 'plan_cache_mode', 'jit', 'max_parallel_workers_per_gather', 'default_statistics_target', 'shared_buffers') ORDER BY name";
            await using var rows = await command.ExecuteReaderAsync(deadline.Token);
            while (await rows.ReadAsync(deadline.Token))
                Write($"DB setting {rows.GetString(0)}={rows.GetString(1)}");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Write($"Count plan diagnostic unavailable: {error.GetType().Name}"); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        { await _stop.CancelAsync(); }
        catch (Exception error) { Write($"Diagnostic cancellation unavailable: {error.GetType().Name}"); }
        try
        { await Task.WhenAll(_monitor, _plan); }
        catch (Exception error) { Write($"Diagnostic task unavailable: {error.GetType().Name}"); }
        try
        { await SnapshotAsync("after"); }
        finally { _stop.Dispose(); }
    }

    internal sealed class ObservedCommands(ScaleQueryDiagnostics owner, int reader) : DbCommandInterceptor
    {
        public string Phase { get; set; } = "starting";
        private int _pid;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _pid = command.Connection is NpgsqlConnection { State: ConnectionState.Open } connection ? connection.ProcessID : 0;
            owner.Write($"SQL reader={reader}, phase={Phase}, pid={_pid}, command={eventData.CommandId}, timeout={command.CommandTimeout}s: begin");
            if (Phase.EndsWith(":counts", StringComparison.Ordinal))
            {
                try
                { owner.CaptureCount(command); }
                catch (Exception error) { owner.Write($"Count capture unavailable: {error.GetType().Name}"); }
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            owner.Write($"SQL reader={reader}, phase={Phase}, pid={_pid}, command={eventData.CommandId}: executed {eventData.Duration.TotalMilliseconds:F1}ms");
            return ValueTask.FromResult(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.Write($"SQL reader={reader}, phase={Phase}, pid={_pid}, command={eventData.CommandId}: failed {eventData.Duration.TotalMilliseconds:F1}ms ({eventData.Exception.GetType().Name})");
            return Task.CompletedTask;
        }
    }
}
