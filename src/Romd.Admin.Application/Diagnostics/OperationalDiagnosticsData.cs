using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Diagnostics;

public static class OperationalDiagnosticsPolicy
{
    public const int CatalogProjectionLimit = 500;
    public const int JobLimit = 100;
    public const int HangfireServerLimit = 100;
    public const int RecurringJobLimit = 100;
    public const int HangfireServerQueueLimit = 100;
    public const int AttemptErrorLimit = 1_000;
    public const int ProviderTextLimit = 500;
    public const int ErrorTextLimit = 2_000;
    public static readonly TimeSpan ReplaceDatStalledAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BulkEnrichmentStrandedAfter = TimeSpan.FromMinutes(10);
}

public sealed class OperationalDiagnosticsOptions
{
    public TimeSpan SectionBudget { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan OverallBudget { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record BoundedDiagnosticsData<T>(IReadOnlyList<T> Items, bool IsTruncated);

public sealed record CatalogProjectionDiagnosticsData(
    string SystemKey,
    string PlatformName,
    string PlatformShortName,
    CatalogRebuildState State,
    DateTimeOffset? RebuiltAt,
    string? LastError,
    DateTimeOffset? FailedAt);

public sealed record ReplaceDatJobDiagnosticsData(
    Guid JobId,
    int ExistingDatId,
    string? SystemKey,
    ReplaceDatPhase Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastProgressAt,
    string? AttemptError,
    bool AttemptErrorTruncated);

public sealed record BulkEnrichmentJobDiagnosticsData(
    Guid JobId,
    string? SystemKey,
    string PlatformLabel,
    BulkEnrichmentPhase Phase,
    DateTimeOffset CreatedAt);

public sealed record OperationalJobDiagnosticsData(
    BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData> ReplaceDatJobs,
    BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData> BulkEnrichmentJobs);

public sealed record OutboxDiagnosticsData(
    int PendingCount,
    DateTimeOffset? OldestPendingAt,
    int FailingCount,
    DateTimeOffset? LastProcessedAt);

public sealed record HangfireServerDiagnosticsData(
    string Name,
    int WorkerCount,
    IReadOnlyList<string> Queues,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt);

public sealed record RecurringJobDiagnosticsData(
    string Id,
    string Queue,
    string Cron,
    DateTimeOffset? LastExecutionAt,
    DateTimeOffset? NextExecutionAt,
    string? LastJobState,
    string? Error);

public sealed record QueueBacklogDiagnosticsData(string Queue, long EnqueuedCount, long FetchedCount);

public sealed record HangfireDiagnosticsData(
    bool IsAvailable,
    string? Error,
    BoundedDiagnosticsData<HangfireServerDiagnosticsData> Servers,
    BoundedDiagnosticsData<RecurringJobDiagnosticsData> RecurringJobs,
    IReadOnlyList<QueueBacklogDiagnosticsData> QueueBacklogs);

public sealed record StorageDiagnosticsData(
    bool IsDataVolumeAvailable,
    long? DataVolumeFreeBytes,
    bool IsCasAvailable,
    string? Error);
