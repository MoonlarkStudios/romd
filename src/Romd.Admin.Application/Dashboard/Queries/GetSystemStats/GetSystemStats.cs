using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Titles;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Dashboard.Queries.GetSystemStats;

/// <summary>
///     Query to get unified system dashboard statistics.
/// </summary>
public sealed record GetSystemStatsQuery : IQuery<SystemStats>;

/// <summary>
///     Handler for <see cref="GetSystemStatsQuery" />.
/// </summary>
public sealed class GetSystemStatsQueryHandler(
    IReferenceCatalogService referenceCatalog,
    IRomRepository romRepository,
    ITitleRepository titleRepository,
    IPlatformRepository platformRepository)
    : IQueryHandler<GetSystemStatsQuery, SystemStats>
{
    public async Task<ErrorOr<SystemStats>> HandleAsync(GetSystemStatsQuery query, CancellationToken ct = default)
    {
        // Sequential awaits: the repositories share one scoped DbContext, which is single-flight
        // (ADR docs/decisions/admin-use-case-transaction-event-boundaries.md, "DbContext Concurrency").
        // Reads are per-query consistent only; cross-query consistency is not promised.
        var libraryStats = await romRepository.GetStatsAsync(ct);
        var enrichmentStats = await titleRepository.GetEnrichmentStatsAsync(ct);
        var platforms = await platformRepository.GetAllAsync(ct);
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);

        // Calculate title totals from platform breakdown
        int totalTitles = libraryStats.PlatformBreakdown.Sum(p => p.TotalCount);
        int titlesWithLocalPayload = libraryStats.PlatformBreakdown.Sum(p => p.LocalPayloadCount);

        // Map platform breakdown to summary format
        var platformSummaries = libraryStats.PlatformBreakdown.Select(p => new PlatformSummary
        {
            SystemKey = systemKeys.Required(p.PlatformId),
            Name = p.PlatformName,
            TotalTitles = p.TotalCount,
            LocalPayloadTitles = p.LocalPayloadCount
        }).ToList();

        return new SystemStats
        {
            TotalRomFiles = libraryStats.TotalRomFiles,
            CatalogedCount = libraryStats.CatalogedCount,
            UnidentifiedCount = libraryStats.UnidentifiedCount,
            UnroutedCount = libraryStats.UnroutedCount,
            TotalSizeBytes = (ByteCount)libraryStats.TotalSizeBytes,
            TotalTitles = totalTitles,
            TitlesWithLocalPayload = titlesWithLocalPayload,
            Enrichment = new EnrichmentStats
            {
                Pending = enrichmentStats.Pending,
                Completed = enrichmentStats.Completed,
                NotFound = enrichmentStats.NotFound,
                Failed = enrichmentStats.Failed
            },
            TotalPlatforms = platforms.Count,
            Platforms = platformSummaries
        };
    }
}
