using System.Text.Json;
using Romd.Domain.Jobs;
namespace Romd.Persistence.Entities;

public sealed class ArtworkCompletionJobEntity : JobEntity
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public string ScopeJson { get; set; } = "{}";
    public ArtworkCompletionJob ToDomain() => WithNextAttempt(ArtworkCompletionJob.Restore(Id, CorrelationId, Total, Processed, Phase,
        HangfireJobId, CreatedAt, StartedAt, CompletedAt, IsArchived, ArchivedAt, CreatedByUserId, CurrentItem,
        JsonSerializer.Deserialize<JobError[]>(ErrorsJson) ?? [], ReadMode(ScopeJson)));
    public static string? ReadMode(string scopeJson)
    {
        using var scope = JsonDocument.Parse(scopeJson);
        return scope.RootElement.TryGetProperty("Mode", out var mode) && mode.ValueKind == JsonValueKind.String
            && mode.GetString() is "Enrich" or "Artwork" ? mode.GetString() : null;
    }
    public static ArtworkCompletionJobEntity FromDomain(ArtworkCompletionJob job) => new()
    {
        ScopeJson = JsonSerializer.Serialize(new { Mode = job.Mode }),
        Id = job.Id,
        CorrelationId = job.CorrelationId,
        Total = job.Total,
        Processed = job.Processed,
        Phase = job.Phase,
        SourceFilename = job.SourceFilename,
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
        JobType = "artwork-completion"
    };
}
