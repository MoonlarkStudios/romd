namespace Romd.ScaleHarness;

public sealed record QueryPlan(string Sql, string Plan, bool HasSeqScan, bool HasSort);

/// <summary>PostgreSQL counters for the harness database, read from pg_stat_database.</summary>
public sealed record DatabaseStats(
    long DatabaseBytes,
    long BlocksRead,
    long BlocksHit,
    long TuplesReturned,
    long TuplesFetched,
    long TempBytes,
    long Transactions);

public sealed record CaseResult(
    string Scenario,
    string Case,
    int Iterations,
    int SucceededIterations,
    long Rows,
    double P50Ms,
    double P95Ms,
    double MeanMs,
    IReadOnlyList<double> LatenciesMs,
    long AllocatedBytesP50,
    long WorkingSetPeakBytes,
    DatabaseStats StatsBefore,
    DatabaseStats StatsAfter,
    IReadOnlyList<QueryPlan> QueryPlans,
    IReadOnlyList<string> Notes,
    int MaxParametersPerCommand,
    int CommandCount);

public sealed record ScenarioReport(string Name, IReadOnlyList<CaseResult> Cases);

public sealed record HardwareInfo(
    string Machine,
    string Os,
    string Framework,
    int ProcessorCount,
    long TotalMemoryBytes);

public sealed record HarnessReport(
    int ReportVersion,
    int DatasetSpecVersion,
    DateTimeOffset GeneratedAt,
    string Scale,
    string Database,
    string PostgresVersion,
    HardwareInfo Hardware,
    DatasetManifest Dataset,
    IReadOnlyList<ScenarioReport> Scenarios);
