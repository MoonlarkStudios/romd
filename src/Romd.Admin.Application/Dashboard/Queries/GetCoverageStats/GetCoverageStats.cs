using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Dashboard.Queries.GetCoverageStats;

public sealed record GetCoverageStatsQuery : IQuery<CoverageStatsDto>;

public sealed class GetCoverageStatsQueryHandler(
    IRomRepository romRepository,
    IDatRepository datRepository)
    : IQueryHandler<GetCoverageStatsQuery, CoverageStatsDto>
{
    public async Task<ErrorOr<CoverageStatsDto>> HandleAsync(GetCoverageStatsQuery query,
        CancellationToken ct = default)
    {
        // Sequential awaits: the repositories share one scoped DbContext, which is single-flight
        // (ADR docs/decisions/admin-use-case-transaction-event-boundaries.md, "DbContext Concurrency").
        // Reads are per-query consistent only; cross-query consistency is not promised.
        var coverage = await romRepository.GetCoverageBreakdownAsync(ct);
        var dats = await datRepository.GetAllAsync(ct);

        decimal coverageHealthPercent = coverage.ExpectedTitleCount > 0
            ? Math.Round((decimal)coverage.LocalPayloadTitleCount / coverage.ExpectedTitleCount * 100, 1)
            : 0m;

        var lastDatRefreshAt = dats.Count > 0
            ? dats.Max(d => d.ImportedAt)
            : (DateTimeOffset?)null;

        return new CoverageStatsDto
        {
            ExpectedTitleCount = coverage.ExpectedTitleCount,
            LocalPayloadTitleCount = coverage.LocalPayloadTitleCount,
            CompleteTitleCount = coverage.CompleteTitleCount,
            PartialTitleCount = coverage.PartialTitleCount,
            CoverageHealthPercent = coverageHealthPercent,
            LastDatRefreshAt = lastDatRefreshAt
        };
    }
}
