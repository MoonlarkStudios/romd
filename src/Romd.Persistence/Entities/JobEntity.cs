using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public abstract class JobEntity : GuidEntityBase
{
    public Guid CorrelationId { get; set; }
    public string SourceFilename { get; set; } = null!;
    public int? PlatformId { get; set; }
    public string Phase { get; set; } = null!;
    public string? HangfireJobId { get; set; }
    public string? CurrentItem { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public string? LastAttemptError { get; set; }
    public bool LastAttemptErrorTruncated { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ExecutionFenceToken { get; set; }
    public DateTimeOffset? ExecutionLeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public int DeferralCount { get; set; }
    public string? LastDeferralError { get; set; }
    public DateTimeOffset? LastDeferralAt { get; set; }
    public DateTimeOffset? FirstDeferralAt { get; set; }
    public string JobType { get; set; } = null!;

    /// <summary>Exposes the persisted deferral schedule on a rehydrated job without making it writable state.</summary>
    protected TJob WithNextAttempt<TJob>(TJob job) where TJob : Job
    {
        job.NextAttemptAt = NextAttemptAtUtc;
        job.DeferralCount = DeferralCount;
        job.LastDeferralError = LastDeferralError;
        job.LastDeferralAt = LastDeferralAt;
        job.FirstDeferralAt = FirstDeferralAt;
        return job;
    }
}
