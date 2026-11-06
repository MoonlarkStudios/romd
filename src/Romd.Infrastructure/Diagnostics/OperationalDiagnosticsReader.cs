using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Diagnostics;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Diagnostics;

public sealed class OperationalDiagnosticsReader(RomdDbContext context, TimeProvider timeProvider)
    : IOperationalDiagnosticsReader
{
    private static readonly string[] ReplaceDatActivePhases =
    [
        ReplaceDatPhase.Ingesting.ToString(),
        ReplaceDatPhase.Replacing.ToString()
    ];

    public async Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> ReadCatalogProjectionsAsync(
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await context.Platforms
            .OrderByDescending(platform => platform.CatalogRebuildState)
            .ThenBy(platform => platform.Name)
            .ThenBy(platform => platform.Id)
            .Take(limit + 1)
            .Select(platform => new CatalogProjectionDiagnosticsData(
                platform.CanonicalKey!,
                platform.Name,
                platform.ShortName,
                platform.CatalogRebuildState,
                platform.CatalogRebuiltAt,
                platform.CatalogRebuildError,
                platform.CatalogRebuildFailedAtUtc))
            .ToListAsync(ct);

        return Bounded(rows, limit);
    }

    public async Task<OperationalJobDiagnosticsData> ReadJobsAsync(
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var now = timeProvider.GetUtcNow();
        var replaceCutoff = now - OperationalDiagnosticsPolicy.ReplaceDatStalledAfter;
        var bulkCutoff = now - OperationalDiagnosticsPolicy.BulkEnrichmentStrandedAfter;

        var replaceRows = await context.Jobs
            .OfType<ReplaceDatJobEntity>()
            .Where(job => ReplaceDatActivePhases.Contains(job.Phase))
            .Where(job => job.StartedAt != null && job.UpdatedAt < replaceCutoff)
            .OrderBy(job => job.UpdatedAt)
            .ThenBy(job => job.Id)
            .Take(limit + 1)
            .Select(job => new
            {
                job.Id,
                job.ExistingDatId,
                SystemKey = context.Platforms.Where(p => p.Id == job.PlatformId).Select(p => p.CanonicalKey).SingleOrDefault(),
                job.Phase,
                job.CreatedAt,
                job.StartedAt,
                job.UpdatedAt,
                job.LastAttemptError,
                job.LastAttemptErrorTruncated
            })
            .ToListAsync(ct);

        var bulkRows = await context.Jobs
            .OfType<BulkEnrichmentJobEntity>()
            .Where(job => job.Phase == nameof(BulkEnrichmentPhase.Pending))
            .Where(job => job.HangfireJobId == null || job.HangfireJobId == string.Empty)
            .Where(job => job.CreatedAt < bulkCutoff)
            .OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Take(limit + 1)
            .Select(job => new
            {
                job.Id,
                SystemKey = context.Platforms.Where(p => p.Id == job.PlatformId).Select(p => p.CanonicalKey).SingleOrDefault(),
                job.SourceFilename,
                job.CreatedAt
            })
            .ToListAsync(ct);

        var replace = replaceRows.Select(row => new ReplaceDatJobDiagnosticsData(
            row.Id,
            row.ExistingDatId,
            row.SystemKey,
            Enum.Parse<ReplaceDatPhase>(row.Phase),
            row.CreatedAt,
            row.StartedAt,
            row.UpdatedAt,
            row.LastAttemptError,
            row.LastAttemptErrorTruncated)).ToList();
        var bulk = bulkRows.Select(row => new BulkEnrichmentJobDiagnosticsData(
            row.Id,
            row.SystemKey,
            row.SourceFilename,
            BulkEnrichmentPhase.Pending,
            row.CreatedAt)).ToList();

        return new OperationalJobDiagnosticsData(Bounded(replace, limit), Bounded(bulk, limit));
    }

    public async Task<OutboxDiagnosticsData> ReadOutboxAsync(CancellationToken ct = default)
    {
        var pending = context.AdminRealtimeOutboxEvents.Where(row => row.ProcessedAtUtc == null);

        int pendingCount = await pending.CountAsync(ct);
        DateTimeOffset? oldestPendingAt = await pending
            .OrderBy(row => row.CreatedAtUtc)
            .Select(row => (DateTimeOffset?)row.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        int failingCount = await pending.CountAsync(row => row.Attempts > 0 || row.LastError != null, ct);
        DateTimeOffset? lastProcessedAt = await context.AdminRealtimeOutboxEvents
            .Where(row => row.ProcessedAtUtc != null)
            .MaxAsync(row => row.ProcessedAtUtc, ct);

        return new OutboxDiagnosticsData(pendingCount, oldestPendingAt, failingCount, lastProcessedAt);
    }

    private static BoundedDiagnosticsData<T> Bounded<T>(IReadOnlyList<T> rows, int limit) =>
        new(rows.Take(limit).ToList(), rows.Count > limit);
}
