using System.Text.Json;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class MaterializationJobEntity : JobEntity
{
    public int LibraryId { get; set; }
    public int TotalTitles { get; set; }
    public int ProcessedCount { get; set; }
    public int IncludedCount { get; set; }
    public int ExcludedCount { get; set; }

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

    public MaterializationJob ToDomain()
    {
        if (!Enum.TryParse<MaterializationPhase>(Phase, out var phase))
            phase = MaterializationPhase.Pending;

        return MaterializationJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            hangfireJobId: HangfireJobId,
            libraryId: LibraryId,
            totalTitles: TotalTitles,
            processedCount: ProcessedCount,
            includedCount: IncludedCount,
            excludedCount: ExcludedCount,
            currentItem: CurrentItem,
            errors: GetErrors(),
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId);
    }

    public static MaterializationJobEntity FromDomain(MaterializationJob domain, DateTimeOffset? updatedAt = null)
    {
        return new MaterializationJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            HangfireJobId = domain.HangfireJobId,
            LibraryId = domain.LibraryId,
            TotalTitles = domain.TotalTitles,
            ProcessedCount = domain.ProcessedCount,
            IncludedCount = domain.IncludedCount,
            ExcludedCount = domain.ExcludedCount,
            CurrentItem = domain.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(domain.Errors.ToList()),
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "materialization"
        };
    }
}
