using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class ArtworkImportJobEntity : JobEntity
{
    public int TitleId { get; set; }
    public ArtworkRole Role { get; set; }
    public long SelectionRevision { get; set; }
    public string ProviderId { get; set; } = null!;
    public string ProviderGameId { get; set; } = null!;
    public string ProviderAssetId { get; set; } = null!;
    public string TrustedAssetUrl { get; set; } = null!;
    public string? Attribution { get; set; }
    public int FocalX { get; set; } = 50;
    public int FocalY { get; set; } = 50;
    public int? RetainedAssetId { get; set; }
    public bool WasSuperseded { get; set; }

    public ArtworkImportJob ToDomain()
    {
        // Unknown persisted phases must never restart an import as Pending.
        if (!Enum.TryParse<ArtworkImportPhase>(Phase, out var phase) || !Enum.IsDefined(phase))
            throw new InvalidOperationException($"Unknown artwork import phase '{Phase}'.");
        var errors = JsonSerializer.Deserialize<List<JobError>>(ErrorsJson) ?? [];
        return ArtworkImportJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            hangfireJobId: HangfireJobId,
            titleId: TitleId,
            role: Role,
            selectionRevision: SelectionRevision,
            providerId: ProviderId,
            providerGameId: ProviderGameId,
            providerAssetId: ProviderAssetId,
            trustedAssetUrl: TrustedAssetUrl,
            attribution: Attribution,
            retainedAssetId: RetainedAssetId,
            wasSuperseded: WasSuperseded,
            currentItem: CurrentItem,
            errors: errors,
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId, focalX: FocalX, focalY: FocalY);
    }

    public static ArtworkImportJobEntity FromDomain(ArtworkImportJob job, DateTimeOffset? updatedAt = null) => new()
    {
        Id = job.Id,
        CorrelationId = job.CorrelationId,
        SourceFilename = job.SourceFilename,
        PlatformId = job.PlatformId,
        Phase = job.Phase,
        HangfireJobId = job.HangfireJobId,
        TitleId = job.TitleId,
        Role = job.Role,
        SelectionRevision = job.SelectionRevision,
        ProviderId = job.ProviderId,
        ProviderGameId = job.ProviderGameId,
        ProviderAssetId = job.ProviderAssetId,
        TrustedAssetUrl = job.TrustedAssetUrl,
        Attribution = job.Attribution,
        FocalX = job.FocalX, FocalY = job.FocalY,
        RetainedAssetId = job.RetainedAssetId,
        WasSuperseded = job.WasSuperseded,
        CurrentItem = job.CurrentItem,
        ErrorsJson = JsonSerializer.Serialize(job.Errors.ToList()),
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        IsArchived = job.IsArchived,
        ArchivedAt = job.ArchivedAt,
        CreatedByUserId = job.CreatedByUserId,
        UpdatedAt = updatedAt ?? job.CreatedAt,
        JobType = "artwork-import"
    };
}
