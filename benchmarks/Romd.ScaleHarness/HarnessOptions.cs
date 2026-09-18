using Npgsql;

namespace Romd.ScaleHarness;

public sealed record HarnessOptions(
    ScaleParameters Scale,
    string Scenario,
    string DataDirectory,
    string ConnectionString,
    bool Regenerate)
{
    public static readonly string[] ScenarioNames =
        ["title-matcher", "title-search", "catalog-filters", "export", "job-items", "materialization", "concurrency",
         "payload-availability"];

    /// <summary>One database per scale on the configured server, never the checkout's dev database.</summary>
    public string DatabaseName => $"romd_scale_{Scale.Name}";

    public string ScaleConnectionString => WithDatabase(ConnectionString, DatabaseName);

    public string MaintenanceConnectionString => WithDatabase(ConnectionString, "postgres");

    public string ManifestPath => Path.Combine(DataDirectory, $"romd-scale-{Scale.Name}.manifest.json");

    /// <summary>Server and database without credentials, for reports.</summary>
    public string DatabaseDescription
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(ScaleConnectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
    }

    public static HarnessOptions Parse(string[] args)
    {
        var scale = ScaleParameters.Scale100K;
        string scenario = "all";
        string dataDir = Path.Combine(Path.GetTempPath(), "romd-scale-harness");
        string? connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Romd");
        bool regenerate = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scale":
                    scale = ScaleParameters.Parse(RequireValue(args, ref i));
                    break;
                case "--scenario":
                    scenario = RequireValue(args, ref i).ToLowerInvariant();
                    break;
                case "--data-dir":
                    dataDir = Path.GetFullPath(RequireValue(args, ref i));
                    break;
                case "--connection-string":
                    connectionString = RequireValue(args, ref i);
                    break;
                case "--regenerate":
                    regenerate = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --help for usage.");
            }
        }

        if (scenario != "all" && !ScenarioNames.Contains(scenario))
        {
            throw new ArgumentException(
                $"Unknown scenario '{scenario}'. Expected one of: all, {string.Join(", ", ScenarioNames)}.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "No PostgreSQL server configured: pass --connection-string or run inside a mise shell " +
                "(ConnectionStrings__Romd is derived by scripts/db/env.sh after `mise run db:up`).");
        }

        return new HarnessOptions(scale, scenario, dataDir, connectionString, regenerate);
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{args[i]}'.");
        }

        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Romd.ScaleHarness — admin workload scale spike (issue #98, PostgreSQL re-baseline #128)

            Usage:
              dotnet run --project benchmarks/Romd.ScaleHarness -- [options]

            Options:
              --scale 100k|1m              Dataset scale (default: 100k)
              --scenario <name>            One scenario or 'all' (default: all)
              --connection-string <value>  PostgreSQL server; the harness creates romd_scale_<scale>
                                           on it (default: ConnectionStrings__Romd from the mise shell)
              --data-dir <path>            Directory for dataset manifests
                                           (default: <system temp>/romd-scale-harness)
              --regenerate                 Drop and regenerate the dataset even if a matching one exists
            """);
    }
}
