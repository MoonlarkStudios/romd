using System.Text.Json;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class EnrichmentJobEntity : JobEntity
{
    public int TitleId { get; set; }
    public bool ArtworkOnly { get; set; }

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

    public EnrichmentJob ToDomain()
    {
        if (!Enum.TryParse<EnrichmentJobPhase>(Phase, out var phase))
        {
            phase = EnrichmentJobPhase.Pending;
        }

        return EnrichmentJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            hangfireJobId: HangfireJobId,
            titleId: TitleId,
            currentItem: CurrentItem,
            errors: GetErrors(),
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId, artworkOnly: ArtworkOnly);
    }

    public static EnrichmentJobEntity FromDomain(EnrichmentJob domain, DateTimeOffset? updatedAt = null)
    {
        return new EnrichmentJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            HangfireJobId = domain.HangfireJobId,
            TitleId = domain.TitleId,
            ArtworkOnly = domain.ArtworkOnly,
            CurrentItem = domain.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(domain.Errors.ToList()),
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "enrichment"
        };
    }
}
