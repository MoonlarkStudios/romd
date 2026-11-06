using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 5: <c>JobItemRepository</c> — paged reads plus the complete streamed export against
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
                var repository = services.GetRequiredService<IJobItemRepository>();
                await using var transaction = await services.GetRequiredService<IReadSnapshotTransactionFactory>()
                    .BeginAsync(ct);
                var result = await repository.GetByJobAsync(bigJobId, null, 100, ct);
                await transaction.CompleteAsync(ct);
                return result.Items.Count;
            },
            cancellationToken);

        var export = await runner.RunCaseAsync(Name, "stream-all-job-items", 3,
            async (services, ct) =>
            {
                var handler = services.GetRequiredService<
                    IQueryHandler<ExportJobItemsQuery, JobItemExport>>();
                var result = await handler.HandleAsync(new ExportJobItemsQuery(bigJobId), ct);
                await using var lease = result.Value;
                long rows = 0;
                await foreach (var _ in lease.ReadAsync(ct))
                {
                    rows++;
                }
                await lease.CompleteAsync(ct);
                return rows;
            },
            cancellationToken);

        return new ScenarioReport(Name, [page, export]);
    }
}
