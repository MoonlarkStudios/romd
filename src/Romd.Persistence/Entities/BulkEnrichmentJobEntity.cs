using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class BulkEnrichmentJobEntity : JobEntity
{
    public string Scope { get; set; } = EnrichmentScope.Tracked.ToString();
    public int TotalTitles { get; set; }
    public int ProcessedCount { get; set; }
    public int EnrichedCount { get; set; }
    public int NotFoundCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }

    public IReadOnlyList<JobError> GetErrors()
    {
        try
        {
            return JsonSerializer.Deserialize<List<JobError>>(ErrorsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public BulkEnrichmentJob ToDomain()
    {
        if (!Enum.TryParse<BulkEnrichmentPhase>(Phase, out var phase))
        {
            phase = BulkEnrichmentPhase.Pending;
        }

        if (!Enum.TryParse<EnrichmentScope>(Scope, out var scope))
        {
            scope = EnrichmentScope.Tracked;
        }

        return BulkEnrichmentJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            scope: scope,
            hangfireJobId: HangfireJobId,
            totalTitles: TotalTitles,
            processedCount: ProcessedCount,
            enrichedCount: EnrichedCount,
            notFoundCount: NotFoundCount,
            failedCount: FailedCount,
            skippedCount: SkippedCount,
            currentItem: CurrentItem,
            errors: GetErrors(),
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId);
    }

    public static BulkEnrichmentJobEntity FromDomain(BulkEnrichmentJob domain, DateTimeOffset? updatedAt = null)
    {
        return new BulkEnrichmentJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            Scope = domain.Scope.ToString(),
            HangfireJobId = domain.HangfireJobId,
            TotalTitles = domain.TotalTitles,
            ProcessedCount = domain.ProcessedCount,
            EnrichedCount = domain.EnrichedCount,
            NotFoundCount = domain.NotFoundCount,
            FailedCount = domain.FailedCount,
            SkippedCount = domain.SkippedCount,
            CurrentItem = domain.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(domain.Errors.ToList()),
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "bulk_enrichment"
        };
    }
}
