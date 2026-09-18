using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Romd.ScaleHarness;
using Romd.ScaleHarness.Scenarios;

var options = HarnessOptions.Parse(args);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine($"Scale:    {options.Scale.Name} ({options.Scale.DatGames:N0} DatGames, {options.Scale.Titles:N0} Titles, {options.Scale.DatRoms:N0} DatRoms)");
Console.WriteLine($"Database: {options.DatabaseDescription}");

var manifest = await DatasetManifest.TryReadAsync(options.ManifestPath, shutdown.Token);
bool databaseExists = await DatasetGenerator.DatabaseExistsAsync(options, shutdown.Token);
if (options.Regenerate
    || manifest is null
    || !manifest.Matches(options.Scale, options.DatabaseName)
    || !databaseExists)
{
    Console.WriteLine($"Generating dataset (spec v{ScaleParameters.SpecVersion})...");
    var generator = new DatasetGenerator(options);
    manifest = await generator.GenerateAsync(shutdown.Token);
    Console.WriteLine($"Dataset generated in {manifest.GenerationSeconds:F1}s ({manifest.RowCounts.Values.Sum():N0} rows).");
}
else
{
    Console.WriteLine($"Reusing dataset generated {manifest.GeneratedAt:O} (spec v{manifest.SpecVersion}).");
}

var scenarios = new IScenario[]
{
    new TitleMatcherScenario(),
    new TitleSearchScenario(),
    new CatalogFiltersScenario(),
    new ExportScenario(),
    new JobItemsScenario(),
    new MaterializationScenario(),
    new ConcurrencyScenario(),
    new PayloadAvailabilityScenario()
};

var selected = options.Scenario == "all"
    ? scenarios
    : scenarios.Where(s => s.Name == options.Scenario).ToArray();

await using var provider = HarnessServices.Build(options.ScaleConnectionString);
var context = new ScenarioContext(
    provider,
    provider.GetRequiredService<SqlCaptureInterceptor>(),
    new DeterministicDataset(options.Scale),
    manifest,
    options.ScaleConnectionString);
string postgresVersion = await DatasetGenerator.ServerVersionAsync(options, shutdown.Token);

var reports = new List<ScenarioReport>();
foreach (var scenario in selected)
{
    Console.WriteLine($"Running scenario: {scenario.Name}...");
    var report = await scenario.RunAsync(context, shutdown.Token);
    reports.Add(report);
    foreach (var c in report.Cases)
    {
        Console.WriteLine(c.SucceededIterations == 0
            ? $"  {c.Case,-42} FAILED: {c.Notes.FirstOrDefault() ?? "no successful iterations"}"
            : $"  {c.Case,-42} rows={c.Rows,10:N0}  p50={c.P50Ms,10:N1}ms  " +
              $"p95={c.P95Ms,10:N1}ms  commands={c.CommandCount,4:N0}  " +
              $"max-params={c.MaxParametersPerCommand,5:N0}");
    }
}

var harnessReport = new HarnessReport(
    ReportVersion: 5,
    DatasetSpecVersion: ScaleParameters.SpecVersion,
    GeneratedAt: DateTimeOffset.UtcNow,
    Scale: options.Scale.Name,
    Database: options.DatabaseDescription,
    PostgresVersion: postgresVersion,
    Hardware: new HardwareInfo(
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
    Dataset: manifest,
    Scenarios: reports);

(string jsonPath, string markdownPath) = await ReportWriter.WriteAsync(
    harnessReport, ResolveResultsDirectory(), shutdown.Token);

Console.WriteLine();
Console.WriteLine($"JSON report:     {jsonPath}");
Console.WriteLine($"Markdown report: {markdownPath}");

var failedCases = reports
    .SelectMany(r => r.Cases)
    .Where(c => c.SucceededIterations < c.Iterations)
    .ToList();
if (failedCases.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"FAILED: {failedCases.Count} case(s) had failing iterations — see Notes in the report:");
    foreach (var c in failedCases)
    {
        Console.WriteLine($"  {c.Scenario}/{c.Case}: {c.SucceededIterations}/{c.Iterations} iterations succeeded");
    }

    return 1;
}

return 0;

// Results always land in benchmarks/Romd.ScaleHarness/results/ regardless of invocation cwd:
// walk up from the binary location to the project directory.
static string ResolveResultsDirectory()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Romd.ScaleHarness.csproj")))
    {
        directory = directory.Parent;
    }

    string root = directory?.FullName ?? Directory.GetCurrentDirectory();
    return Path.Combine(root, "results");
}
