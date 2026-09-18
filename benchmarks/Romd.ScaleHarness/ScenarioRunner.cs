using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Romd.ScaleHarness;

/// <summary>
///     Shared measurement plumbing: p50/p95 wall latency over N iterations, rows returned,
///     allocation deltas, working-set peak, EXPLAIN for the captured production SQL with its bound
///     parameters, and pg_stat_database counters around each case.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly ScenarioContext _context;

    public ScenarioRunner(ScenarioContext context)
    {
        _context = context;
    }

    public async Task<CaseResult> RunCaseAsync(
        string scenario,
        string caseName,
        int iterations,
        Func<IServiceProvider, CancellationToken, Task<long>> operation,
        CancellationToken cancellationToken = default)
    {
        var latencies = new List<double>(iterations);
        var allocations = new List<long>(iterations);
        var notes = new List<string>();
        long rows = 0;
        long workingSetPeak = 0;

        var statsBefore = await ReadDatabaseStatsAsync(cancellationToken);
        IReadOnlyList<CapturedCommand> captured = [];

        for (int i = 0; i < iterations; i++)
        {
            await using var scope = _context.Provider.CreateAsyncScope();

            bool capture = i == 0;
            if (capture)
            {
                _context.SqlCapture.Begin();
            }

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            using var sampleStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workingSetSample = SampleWorkingSetPeakAsync(sampleStop.Token);
            try
            {
                rows = await operation(scope.ServiceProvider, cancellationToken);
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                notes.Add($"iteration {i + 1} failed after {stopwatch.Elapsed.TotalMilliseconds:F1} ms: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await sampleStop.CancelAsync();
                workingSetPeak = Math.Max(workingSetPeak, await workingSetSample);
                if (capture)
                {
                    captured = _context.SqlCapture.End();
                }
            }
        }

        var statsAfter = await ReadDatabaseStatsAsync(cancellationToken);
        var plans = await ExplainAsync(captured, cancellationToken);

        int maxParametersPerCommand = captured.Count == 0
            ? 0
            : captured.Max(command => command.Parameters.Count);

        return BuildResult(scenario, caseName, iterations, rows, latencies, allocations,
            workingSetPeak, statsBefore, statsAfter, plans, notes, maxParametersPerCommand, captured.Count);
    }

    public static CaseResult BuildResult(
        string scenario,
        string caseName,
        int iterations,
        long rows,
        List<double> latencies,
        List<long> allocations,
        long workingSetPeak,
        DatabaseStats statsBefore,
        DatabaseStats statsAfter,
        IReadOnlyList<QueryPlan> plans,
        List<string> notes,
        int maxParametersPerCommand = 0,
        int commandCount = 0)
    {
        return new CaseResult(
            scenario,
            caseName,
            iterations,
            latencies.Count,
            rows,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies.Count == 0 ? 0 : latencies.Average(),
            latencies.Select(l => Math.Round(l, 3)).ToList(),
            (long)Percentile(allocations.Select(a => (double)a).ToList(), 0.50),
            workingSetPeak,
            statsBefore,
            statsAfter,
            plans,
            notes,
            maxParametersPerCommand,
            commandCount);
    }

    public static double Percentile(List<double> values, double quantile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        int rank = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(rank, 0, sorted.Count - 1)], 3);
    }

    public async Task<DatabaseStats> ReadDatabaseStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenRawConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_database_size(current_database()),
                   blks_read, blks_hit, tup_returned, tup_fetched, temp_bytes, xact_commit + xact_rollback
            FROM pg_stat_database
            WHERE datname = current_database()
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DatabaseStats(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    public async Task<IReadOnlyList<QueryPlan>> ExplainAsync(
        IReadOnlyList<CapturedCommand> captured,
        CancellationToken cancellationToken)
    {
        const int maxPlans = 15;
        var plans = new List<QueryPlan>();
        var seen = new HashSet<string>();

        await using var connection = OpenRawConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var command in captured)
        {
            string text = command.Text.Trim();
            if (IsNonPlannable(text) || !seen.Add(text) || plans.Count >= maxPlans)
            {
                continue;
            }

            plans.Add(await ExplainSingleAsync(connection, command, cancellationToken));
        }

        return plans;
    }

    private static bool IsNonPlannable(string sql) =>
        sql.StartsWith("SET ", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("SAVEPOINT", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("RELEASE", StringComparison.OrdinalIgnoreCase) ||
        sql.StartsWith("SELECT pg_advisory", StringComparison.OrdinalIgnoreCase);

    private static async Task<QueryPlan> ExplainSingleAsync(
        NpgsqlConnection connection,
        CapturedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var explain = new NpgsqlCommand("EXPLAIN " + command.Text, connection);
            foreach (var parameter in command.Parameters)
            {
                explain.Parameters.Add(parameter.Value is null
                    ? new NpgsqlParameter(parameter.Name, NpgsqlDbType.Unknown) { Value = DBNull.Value }
                    : new NpgsqlParameter(parameter.Name, parameter.Value));
            }

            var lines = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }

            string plan = string.Join("\n", lines);
            // A sequential scan over a lookup table (platforms, regions) is the planner's right
            // answer; only scans the planner expects to read many rows are worth a flag.
            bool seqScan = lines.Any(l => l.Contains("Seq Scan on", StringComparison.Ordinal)
                && EstimatedRows(l) >= LargeScanRows);
            bool sort = lines.Any(l => l.TrimStart().StartsWith("->  Sort", StringComparison.Ordinal)
                || l.TrimStart().StartsWith("Sort ", StringComparison.Ordinal));

            return new QueryPlan(command.Text, plan, seqScan, sort);
        }
        catch (PostgresException ex)
        {
            return new QueryPlan(command.Text, $"(EXPLAIN failed: {ex.MessageText})", false, false);
        }
    }

    private const long LargeScanRows = 1_000;

    private static long EstimatedRows(string planLine)
    {
        Match match = Regex.Match(planLine, @"rows=(\d+)");
        return match.Success ? long.Parse(match.Groups[1].Value) : 0;
    }

    public NpgsqlConnection OpenRawConnection() => new(_context.ConnectionString);

    private static async Task<long> SampleWorkingSetPeakAsync(CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        long peak = process.WorkingSet64;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            peak = Math.Max(peak, process.WorkingSet64);
        }

        return peak;
    }
}
