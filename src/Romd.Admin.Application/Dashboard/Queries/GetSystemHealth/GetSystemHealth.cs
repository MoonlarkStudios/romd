using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Dashboard.Queries.GetSystemHealth;

public sealed record GetSystemHealthQuery : IQuery<SystemHealthDto>;

public sealed class GetSystemHealthQueryHandler(
    IRomRepository romRepository,
    IJobRepository jobRepository)
    : IQueryHandler<GetSystemHealthQuery, SystemHealthDto>
{
    public async Task<ErrorOr<SystemHealthDto>> HandleAsync(GetSystemHealthQuery query, CancellationToken ct = default)
    {
        // Sequential awaits: the repositories share one scoped DbContext, which is single-flight
        // (ADR docs/decisions/admin-use-case-transaction-event-boundaries.md, "DbContext Concurrency").
        // Reads are per-query consistent only; cross-query consistency is not promised.
        var stats = await romRepository.GetStatsAsync(ct);
        var jobs = await jobRepository.GetRecentAsync(limit: 100, ct: ct);

        int identifiedCount = stats.TotalRomFiles - stats.UnidentifiedCount;
        int failedJobCount = jobs.Count(j => j.HasErrors && j.IsTerminal);

        return new SystemHealthDto
        {
            TotalRoms = stats.TotalRomFiles,
            IdentifiedCount = identifiedCount,
            UnidentifiedCount = stats.UnidentifiedCount,
            RoutedCount = stats.CatalogedCount,
            UnroutedCount = stats.UnroutedCount,
            FailedJobCount = failedJobCount
        };
    }
}
