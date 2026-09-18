using System.Text;
using System.Text.Json;

namespace Romd.ScaleHarness;

/// <summary>
///     Writes the versioned machine-readable JSON report and a human-readable markdown summary
///     into the (gitignored) results directory.
/// </summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        HarnessReport report,
        string resultsDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(resultsDirectory);
        string stamp = report.GeneratedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        string baseName = $"scale-harness-{report.Scale}-{stamp}";

        string jsonPath = Path.Combine(resultsDirectory, baseName + ".json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);

        string markdownPath = Path.Combine(resultsDirectory, baseName + ".md");
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), cancellationToken);

        return (jsonPath, markdownPath);
    }

    private static string BuildMarkdown(HarnessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ROMD scale harness report — {report.Scale}");
        sb.AppendLine();
        sb.AppendLine($"- Report version: {report.ReportVersion}, dataset spec v{report.DatasetSpecVersion}, seed `0x{ScaleParameters.Seed:X}`");
        sb.AppendLine($"- Generated: {report.GeneratedAt:O}");
        sb.AppendLine($"- Machine: {report.Hardware.Machine} — {report.Hardware.Os} — {report.Hardware.Framework} — {report.Hardware.ProcessorCount} cores");
        sb.AppendLine($"- Database: `{report.Database}` on PostgreSQL {report.PostgresVersion} (generation took {report.Dataset.GenerationSeconds:F1}s)");
        sb.AppendLine();

        sb.AppendLine("## Dataset");
        sb.AppendLine();
        sb.AppendLine("| Table | Rows |");
        sb.AppendLine("|---|---:|");
        foreach ((string table, long count) in report.Dataset.RowCounts.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"| {table} | {count:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("Search index checks:");
        foreach (string check in report.Dataset.SearchChecks)
        {
            sb.AppendLine($"- {check}");
        }

        foreach (var scenario in report.Scenarios)
        {
            sb.AppendLine();
            sb.AppendLine($"## Scenario: {scenario.Name}");
            sb.AppendLine();
            sb.AppendLine("| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | WS peak | Commands | Max params | Blocks read | Temp bytes |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var c in scenario.Cases)
            {
                long blocksRead = c.StatsAfter.BlocksRead - c.StatsBefore.BlocksRead;
                long tempBytes = c.StatsAfter.TempBytes - c.StatsBefore.TempBytes;
                string iterations = c.SucceededIterations == c.Iterations
                    ? c.Iterations.ToString()
                    : $"{c.SucceededIterations}/{c.Iterations}";
                sb.AppendLine(c.SucceededIterations == 0
                    ? $"| {c.Case} | {iterations} | FAILED | — | — | — | {FormatBytes(c.WorkingSetPeakBytes)} | {c.CommandCount:N0} | {c.MaxParametersPerCommand:N0} | {blocksRead:N0} | {FormatBytes(tempBytes)} |"
                    : $"| {c.Case} | {iterations} | {c.Rows:N0} | {c.P50Ms:N1} | {c.P95Ms:N1} | " +
                      $"{FormatBytes(c.AllocatedBytesP50)} | {FormatBytes(c.WorkingSetPeakBytes)} | {c.CommandCount:N0} | {c.MaxParametersPerCommand:N0} | {blocksRead:N0} | {FormatBytes(tempBytes)} |");
            }

            var flagged = scenario.Cases
                .SelectMany(c => c.QueryPlans.Where(p => p.HasSeqScan || p.HasSort)
                    .Select(p => (c.Case, Plan: p)))
                .ToList();
            if (flagged.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Query-plan flags (sequential scans / sorts):");
                foreach ((string caseName, var plan) in flagged)
                {
                    string flags = string.Join("+",
                        new[] { plan.HasSeqScan ? "SEQ SCAN" : null, plan.HasSort ? "SORT" : null }
                            .Where(f => f is not null));
                    sb.AppendLine($"- `{caseName}` [{flags}]");
                    foreach (string line in plan.Plan.Split('\n'))
                    {
                        sb.AppendLine($"  - {line}");
                    }
                }
            }

            var notes = scenario.Cases.SelectMany(c => c.Notes.Select(n => (c.Case, Note: n))).ToList();
            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Notes:");
                foreach ((string caseName, string note) in notes)
                {
                    sb.AppendLine($"- `{caseName}`: {note}");
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        double abs = Math.Abs(bytes);
        return abs switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MiB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KiB",
            _ => $"{bytes} B"
        };
    }
}
