using System.Text.Json;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class UploadJobEntity : JobEntity
{
    public int DatsDiscovered { get; set; }
    public int RomsDiscovered { get; set; }
    public int DatsProcessed { get; set; }
    public int DatsSucceeded { get; set; }
    public int RomsProcessed { get; set; }
    public int RomsIngested { get; set; }
    public int RomsDeduplicated { get; set; }
    public int RomsRejected { get; set; }
    public int MaxParallelRoms { get; set; } = 4;
    public bool AllowUnidentified { get; set; }
    public bool ArchiveOnly { get; set; }
    public bool TrackedOnly { get; set; }
    public string? ImportSourcePath { get; set; }
    public bool ImportMove { get; set; }

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

    public UploadJob ToDomain()
    {
        if (!Enum.TryParse<UploadPhase>(Phase, out var phase))
        {
            phase = UploadPhase.Pending;
        }

        return WithNextAttempt(UploadJob.Rehydrate(
            Id,
            CorrelationId,
            SourceFilename,
            PlatformId,
            phase,
            HangfireJobId,
            DatsDiscovered,
            RomsDiscovered,
            DatsProcessed,
            DatsSucceeded,
            RomsProcessed,
            RomsIngested,
            RomsDeduplicated,
            RomsRejected,
            MaxParallelRoms,
            AllowUnidentified,
            ArchiveOnly,
            ImportSourcePath,
            ImportMove,
            CurrentItem,
            GetErrors(),
            CreatedAt,
            StartedAt,
            CompletedAt,
            IsArchived,
            ArchivedAt,
            CreatedByUserId,
            TrackedOnly));
    }

    public void SetIngestionProgress(UploadJob domain)
    {
        DatsProcessed = domain.DatsProcessed;
        DatsSucceeded = domain.DatsSucceeded;
        RomsProcessed = domain.RomsProcessed;
        RomsIngested = domain.RomsIngested;
        RomsDeduplicated = domain.RomsDeduplicated;
        RomsRejected = domain.RomsRejected;
        CurrentItem = domain.CurrentItem;
        ErrorsJson = JsonSerializer.Serialize(domain.Errors);
    }

    public static UploadJobEntity FromDomain(UploadJob domain, DateTimeOffset? updatedAt = null)
    {
        var entity = new UploadJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            HangfireJobId = domain.HangfireJobId,
            DatsDiscovered = domain.DatsDiscovered,
            RomsDiscovered = domain.RomsDiscovered,
            MaxParallelRoms = domain.MaxParallelRoms,
            AllowUnidentified = domain.AllowUnidentified,
            ArchiveOnly = domain.ArchiveOnly,
            TrackedOnly = domain.TrackedOnly,
            ImportSourcePath = domain.ImportSourcePath,
            ImportMove = domain.ImportMove,
            DeferralCount = domain.DeferralCount,
            LastDeferralError = domain.LastDeferralError,
            LastDeferralAt = domain.LastDeferralAt,
            FirstDeferralAt = domain.FirstDeferralAt,
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "upload"
        };
        entity.SetIngestionProgress(domain);
        return entity;
    }
}
