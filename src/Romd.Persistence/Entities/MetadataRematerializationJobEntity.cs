using System.Text.Json;
using Romd.Domain.Jobs;
namespace Romd.Persistence.Entities;

public sealed class MetadataRematerializationJobEntity : JobEntity
{
    public const string Type = "metadata-rematerialization";
    public int TotalTitles { get; set; }
    public int ProcessedCount { get; set; }
    public int DeferredCount { get; set; }
    public int CursorTitleId { get; set; }
    public MetadataRematerializationJob ToDomain() => WithNextAttempt(MetadataRematerializationJob.Restore(
        Id, CorrelationId, PlatformId, SourceFilename, TotalTitles, ProcessedCount, DeferredCount, CursorTitleId,
        Phase, HangfireJobId, CreatedAt, StartedAt, CompletedAt, IsArchived, ArchivedAt, CreatedByUserId,
        CurrentItem, JsonSerializer.Deserialize<JobError[]>(ErrorsJson) ?? []));
    public static MetadataRematerializationJobEntity FromDomain(MetadataRematerializationJob job) => new()
    {
        Id = job.Id,
        CorrelationId = job.CorrelationId,
        PlatformId = job.PlatformId,
        SourceFilename = job.SourceFilename,
        TotalTitles = job.TotalTitles,
        ProcessedCount = job.ProcessedCount,
        DeferredCount = job.DeferredCount,
        CursorTitleId = job.CursorTitleId,
        Phase = job.Phase,
        HangfireJobId = job.HangfireJobId,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        IsArchived = job.IsArchived,
        ArchivedAt = job.ArchivedAt,
        CreatedByUserId = job.CreatedByUserId,
        CurrentItem = job.CurrentItem,
        ErrorsJson = JsonSerializer.Serialize(job.Errors),
        UpdatedAt = DateTimeOffset.UtcNow,
        JobType = Type
    };
}
