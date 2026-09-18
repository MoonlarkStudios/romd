using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Search;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 3: <c>GetCatalogFiltersAsync</c> facet aggregations. The whole production call is
///     measured once as-is, then each captured facet SELECT is replayed sequentially (raw ADO)
///     so the eight aggregations get individual timings — per the #89 ADR the
///     parallel-on-one-context shape is migrating anyway, so sequential per-facet numbers are
///     the durable evidence.
/// </summary>
public sealed class CatalogFiltersScenario : IScenario
{
    public string Name => "catalog-filters";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var cases = new List<CaseResult>();

        var wholeCall = await runner.RunCaseAsync(Name, "whole-call", 3,
            async (services, ct) =>
            {
                var repository = services.GetRequiredService<ISearchRepository>();
                var filters = await repository.GetCatalogFiltersAsync(ct);
                return filters.Genres.Count + filters.Years.Count + filters.Manufacturers.Count +
                       filters.Regions.Count + filters.Languages.Count + filters.ContentRatings.Count +
                       filters.Platforms.Count + filters.EnrichmentStatuses.Count;
            },
            cancellationToken);
        cases.Add(wholeCall);

        // Sequential per-facet replay of the exact SQL EF executed during the whole call.
        var facetSql = wholeCall.QueryPlans
            .Where(p => p.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Sql)
            .ToList();

        for (int i = 0; i < facetSql.Count; i++)
        {
            cases.Add(await ReplayFacetAsync(runner, context, i + 1, facetSql[i], cancellationToken));
        }

        return new ScenarioReport(Name, cases);
    }

    private async Task<CaseResult> ReplayFacetAsync(
        ScenarioRunner runner,
        ScenarioContext context,
        int facetNumber,
        string sql,
        CancellationToken cancellationToken)
    {
        const int iterations = 3;
        var latencies = new List<double>(iterations);
        var allocations = new List<long>(iterations);
        long rows = 0;

        var statsBefore = await runner.ReadDatabaseStatsAsync(cancellationToken);
        var plans = await runner.ExplainAsync([new CapturedCommand(sql, [])], cancellationToken);

        await using var connection = runner.OpenRawConnection();
        await connection.OpenAsync(cancellationToken);

        for (int i = 0; i < iterations; i++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            long count = 0;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    count++;
                }
            }

            stopwatch.Stop();
            rows = count;
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        }

        var statsAfter = await runner.ReadDatabaseStatsAsync(cancellationToken);
        using var process = Process.GetCurrentProcess();

        return ScenarioRunner.BuildResult(
            Name, $"facet-{facetNumber}-sequential", iterations, rows, latencies, allocations,
            process.WorkingSet64, statsBefore, statsAfter, plans,
            [$"raw ADO replay of captured production SQL: {Truncate(sql)}"]);
    }

    private static string Truncate(string sql)
    {
        string flattened = sql.ReplaceLineEndings(" ");
        return flattened.Length <= 120 ? flattened : flattened[..120] + "...";
    }
}
