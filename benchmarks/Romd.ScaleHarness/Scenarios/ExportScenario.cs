using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Export;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 4: <c>ExportRepository.GetExportFilesAsync</c> — both the unbounded
///     all-platform path and the library-scoped path. Captured command metadata proves selected
///     release ids stay within the shared bounded-id query ceiling; memory and latency remain the
///     baseline for #97's eventual streaming redesign.
/// </summary>
public sealed class ExportScenario : IScenario
{
    public string Name => "export";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);

        var unbounded = await runner.RunCaseAsync(Name, "get-export-files-unbounded", 3,
            async (services, ct) =>
            {
                var repository = services.GetRequiredService<IExportRepository>();
                var files = await repository.GetExportFilesAsync(new ExportScope.AllCatalog(), ct);
                return files.Count;
            },
            cancellationToken);

        var scoped = await runner.RunCaseAsync(Name, "get-export-files-library-scoped", 3,
            async (services, ct) =>
            {
                var repository = services.GetRequiredService<IExportRepository>();
                var files = await repository.GetExportFilesAsync(new ExportScope.Library(1, 0), ct);
                return files.Count;
            },
            cancellationToken);

        return new ScenarioReport(Name, [unbounded, scoped]);
    }
}
