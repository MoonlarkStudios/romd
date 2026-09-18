using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 5: <c>JobItemRepository</c> — paged reads plus the 100k-cap export path against
///     the largest synthetic job. Captured command metadata proves title-id resolution stays
///     within the shared bounded-id query ceiling at both scales.
/// </summary>
public sealed class JobItemsScenario : IScenario
{
    public string Name => "job-items";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var bigJobId = context.Manifest.BigJobId;

        var page = await runner.RunCaseAsync(Name, "get-by-job-page-100", 5,
            async (services, ct) =>
            {
                var handler = services.GetRequiredService<IQueryHandler<GetJobItemsQuery, JobItemPageResult>>();
                var result = await handler.HandleAsync(new GetJobItemsQuery(bigJobId, null, 100), ct);
                return result.Value.Items.Count;
            },
            cancellationToken);

        var export = await runner.RunCaseAsync(Name, "get-all-by-job-100k-cap", 3,
            async (services, ct) =>
            {
                var handler = services.GetRequiredService<
                    IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>>>();
                var result = await handler.HandleAsync(new ExportJobItemsQuery(bigJobId), ct);
                return result.Value.Count;
            },
            cancellationToken);

        return new ScenarioReport(Name, [page, export]);
    }
}
