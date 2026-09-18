using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Titles.Matching;
using Romd.Persistence;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 7: reader/writer concurrency. Single process, multiple DbContexts (one per
///     scope/connection): a reader mix plus one writer, matching the one-node worker+API topology.
///     Multi-process forking is out of scope for the spike and recorded as an assumption. Runs a
///     readers-only baseline phase first, then readers + writer, counting serialization failures,
///     deadlocks, and lock timeouts where the SQLite run counted busy/locked errors. Writer
///     mutations (TrackedTitles intent rows) are restored to spec values afterwards.
/// </summary>
public sealed class ConcurrencyScenario : IScenario
{
    private const int ReaderCount = 4;
    private const int WriterBatchSize = 500;
    private static readonly TimeSpan BaselineDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ContentionDuration = TimeSpan.FromSeconds(10);

    public string Name => "concurrency";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var cases = new List<CaseResult>();

        cases.AddRange(await RunPhaseAsync(runner, context, "readers-only", BaselineDuration, withWriter: false, cancellationToken));
        cases.AddRange(await RunPhaseAsync(runner, context, "readers+writer", ContentionDuration, withWriter: true, cancellationToken));

        await RestoreTrackedFlagsAsync(runner, context, cancellationToken);
        return new ScenarioReport(Name, cases);
    }

    private async Task<List<CaseResult>> RunPhaseAsync(
        ScenarioRunner runner,
        ScenarioContext context,
        string phase,
        TimeSpan duration,
        bool withWriter,
        CancellationToken cancellationToken)
    {
        var statsBefore = await runner.ReadDatabaseStatsAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);

        var readerResults = Enumerable.Range(0, ReaderCount).Select(_ => new ReaderMetrics()).ToArray();
        var readerTasks = Enumerable.Range(0, ReaderCount)
            .Select(r => Task.Run(
                () => RunReaderAsync(context, r, readerResults[r], deadline.Token), cancellationToken))
            .ToList();

        var writerMetrics = new WriterMetrics();
        var writerTask = withWriter
            ? Task.Run(() => RunWriterAsync(context, writerMetrics, deadline.Token), cancellationToken)
            : Task.CompletedTask;

        await Task.WhenAll([.. readerTasks, writerTask]);
        var statsAfter = await runner.ReadDatabaseStatsAsync(cancellationToken);

        var cases = new List<CaseResult>();
        foreach (var group in readerResults.SelectMany(m => m.Latencies).GroupBy(l => l.Operation))
        {
            var latencies = group.Select(l => l.Milliseconds).ToList();
            long busy = readerResults.Sum(m => m.BusyErrors);
            cases.Add(ScenarioRunner.BuildResult(
                Name, $"{phase}/reader-{group.Key}", latencies.Count, latencies.Count, latencies, [],
                CurrentWorkingSet(), statsBefore, statsAfter, [],
                [$"{ReaderCount} concurrent reader contexts over {duration.TotalSeconds:F0}s; "
                 + $"total reader contention errors (40001/40P01/55P03) in phase: {busy}",
                 "iterations = completed ops; rows = op count; per-op allocations are not attributable under concurrency and are not measured here"]));
        }

        if (withWriter)
        {
            cases.Add(ScenarioRunner.BuildResult(
                Name, $"{phase}/writer-batches", writerMetrics.Latencies.Count,
                writerMetrics.RowsUpdated, writerMetrics.Latencies, [],
                CurrentWorkingSet(), statsBefore, statsAfter, [],
                [$"tracked-intent mutation batches of {WriterBatchSize} titles per transaction; "
                 + $"contention errors: {writerMetrics.BusyErrors}",
                 "rows = titles updated; per-op allocations are not attributable under concurrency and are not measured here"]));
        }

        return cases;
    }

    private static async Task RunReaderAsync(
        ScenarioContext context,
        int readerIndex,
        ReaderMetrics metrics,
        CancellationToken deadline)
    {
        int iteration = 0;

        while (!deadline.IsCancellationRequested)
        {
            string operation = ((iteration + readerIndex) % 3) switch
            {
                0 => "search-page",
                1 => "job-items-page",
                _ => "matcher-batch-500"
            };

            try
            {
                await using var scope = context.Provider.CreateAsyncScope();
                var stopwatch = Stopwatch.StartNew();
                switch (operation)
                {
                    case "search-page":
                        await scope.ServiceProvider.GetRequiredService<ISearchRepository>().SearchTitlesAsync(
                            context.Manifest.SearchToken, new TitleSearchFilters(), TitleSortField.Name,
                            null, 50, null, deadline);
                        break;
                    case "job-items-page":
                        await scope.ServiceProvider.GetRequiredService<IJobItemRepository>()
                            .GetByJobAsync(context.Manifest.BigJobId, null, 100, deadline);
                        break;
                    default:
                        var names = Enumerable.Range(0, TitleMatchingContract.MaxBatchSize)
                            .Select(context.Data.TitleName)
                            .ToList();
                        await scope.ServiceProvider.GetRequiredService<ITitleMatcher>()
                            .MatchOrCreateBatchAsync(context.Manifest.LargestPlatformId, names, deadline);
                        break;
                }

                stopwatch.Stop();
                metrics.Latencies.Add((operation, stopwatch.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                metrics.BusyErrors++;
            }

            iteration++;
        }
    }

    private static async Task RunWriterAsync(
        ScenarioContext context,
        WriterMetrics metrics,
        CancellationToken deadline)
    {
        int platformTitleCount = context.Data.TitleCountForPlatform(context.Manifest.LargestPlatformId);
        int batch = 0;

        while (!deadline.IsCancellationRequested)
        {
            int start = 1 + batch * WriterBatchSize % Math.Max(1, platformTitleCount - WriterBatchSize);
            bool value = batch % 2 == 0;

            try
            {
                await using var scope = context.Provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();

                var stopwatch = Stopwatch.StartNew();
                int updated;
                if (value)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    updated = await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO romd."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
                        SELECT title."Id", NULL::integer, {now}, {now}
                        FROM romd."Titles" title
                        WHERE title."Id" >= {start} AND title."Id" < {start + WriterBatchSize}
                        ON CONFLICT("TitleId") DO NOTHING;
                        """,
                        deadline);
                }
                else
                {
                    updated = await db.TrackedTitles
                        .Where(t => t.TitleId >= start && t.TitleId < start + WriterBatchSize)
                        .ExecuteDeleteAsync(deadline);
                }
                stopwatch.Stop();

                metrics.Latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                metrics.RowsUpdated += updated;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                metrics.BusyErrors++;
            }

            batch++;
        }
    }

    /// <summary>Restores tracked-title rows to their deterministic spec membership.</summary>
    private static async Task RestoreTrackedFlagsAsync(
        ScenarioRunner runner,
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        await using var connection = runner.OpenRawConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Spec rule: TitleTracked(t) == (t % 3 != 0) with t = TitleId - 1.
        long now = DeterministicDataset.BaseTime.ToUnixTimeMilliseconds();
        command.CommandText =
            "DELETE FROM romd.\"TrackedTitles\" WHERE (\"TitleId\" - 1) % 3 = 0; " +
            "INSERT INTO romd.\"TrackedTitles\" (\"TitleId\", \"PinnedCatalogReleaseId\", \"CreatedAt\", \"UpdatedAt\") " +
            $"SELECT \"Id\", NULL::integer, {now}, {now} FROM romd.\"Titles\" WHERE (\"Id\" - 1) % 3 <> 0 " +
            "ON CONFLICT (\"TitleId\") DO NOTHING;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsBusy(Exception ex) =>
        ex is PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable
        } ||
        (ex.InnerException is not null && IsBusy(ex.InnerException));

    private static long CurrentWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private sealed class ReaderMetrics
    {
        public List<(string Operation, double Milliseconds)> Latencies { get; } = [];
        public long BusyErrors { get; set; }
    }

    private sealed class WriterMetrics
    {
        public List<double> Latencies { get; } = [];
        public long RowsUpdated { get; set; }
        public long BusyErrors { get; set; }
    }
}
