using System.Text.Json.Serialization;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.Serialization;
using Romd.Contracts.Management.Models;

namespace Romd.Contracts.Management.Diagnostics;

public enum CatalogProjectionState
{
    Clean = 0,
    Dirty = 1,
    Failed = 2
}

public enum ReplaceDatDiagnosticPhase
{
    Pending = 0,
    Ingesting = 1,
    Replacing = 2
}

public enum BulkEnrichmentDiagnosticPhase
{
    Pending = 0
}

public enum DiagnosticAvailability
{
    Available = 0,
    Unavailable = 1
}

public enum RecurringJobDiagnosticState
{
    Unknown = 0,
    Awaiting = 1,
    Scheduled = 2,
    Enqueued = 3,
    Processing = 4,
    Succeeded = 5,
    Failed = 6,
    Deleted = 7
}

public sealed record OperationalDiagnostics
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required CatalogProjectionDiagnostics CatalogProjections { get; init; }
    public required OperationalJobDiagnostics Jobs { get; init; }
    public required OutboxDiagnostics Outbox { get; init; }
    public required HangfireDiagnostics Hangfire { get; init; }
    public required StorageDiagnostics Storage { get; init; }
}

public sealed record CatalogProjectionDiagnostics
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<CatalogProjectionDiagnostic> Items { get; init; }
    public required bool IsTruncated { get; init; }
}

public sealed record CatalogProjectionDiagnostic
{
    public required string SystemKey { get; init; }
    public required string PlatformName { get; init; }
    public required string PlatformShortName { get; init; }
    public required CatalogProjectionState State { get; init; }
    public DateTimeOffset? RebuiltAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}

public sealed record OperationalJobDiagnostics
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<WedgedReplaceDatJob> WedgedReplaceDatJobs { get; init; }
    public required bool WedgedReplaceDatJobsTruncated { get; init; }
    public required IReadOnlyList<StrandedBulkEnrichmentJob> StrandedBulkEnrichmentJobs { get; init; }
    public required bool StrandedBulkEnrichmentJobsTruncated { get; init; }
}

public sealed record WedgedReplaceDatJob
{
    public required Guid JobId { get; init; }
    public required string ExistingDatId { get; init; }
    public string? SystemKey { get; init; }
    public required ReplaceDatDiagnosticPhase Phase { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required DateTimeOffset LastProgressAt { get; init; }
    public required long StalledForSeconds { get; init; }
    public string? AttemptError { get; init; }
    public required bool AttemptErrorTruncated { get; init; }
}

public sealed record StrandedBulkEnrichmentJob
{
    public required Guid JobId { get; init; }
    public string? SystemKey { get; init; }
    public required string PlatformLabel { get; init; }
    public required BulkEnrichmentDiagnosticPhase Phase { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long StrandedForSeconds { get; init; }
}

public sealed record OutboxDiagnostics
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required int PendingCount { get; init; }
    public long? OldestPendingAgeSeconds { get; init; }
    public required int FailingCount { get; init; }
    public DateTimeOffset? LastProcessedAt { get; init; }
}

public sealed record HangfireDiagnostics
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<HangfireServerDiagnostic> Servers { get; init; }
    public required bool ServersTruncated { get; init; }
    public required IReadOnlyList<RecurringJobDiagnostic> RecurringJobs { get; init; }
    public required bool RecurringJobsTruncated { get; init; }
    public required IReadOnlyList<QueueBacklogDiagnostic> QueueBacklogs { get; init; }
}

public sealed record HangfireServerDiagnostic
{
    public required string Name { get; init; }
    public required int WorkerCount { get; init; }
    public required IReadOnlyList<string> Queues { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public long? HeartbeatAgeSeconds { get; init; }
}

public sealed record RecurringJobDiagnostic
{
    public required string Id { get; init; }
    public required string Queue { get; init; }
    public required string Cron { get; init; }
    public DateTimeOffset? LastExecutionAt { get; init; }
    public DateTimeOffset? NextExecutionAt { get; init; }
    public RecurringJobDiagnosticState? LastJobState { get; init; }
    public string? Error { get; init; }
}

public sealed record QueueBacklogDiagnostic
{
    public required string Queue { get; init; }
    [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
    public required long EnqueuedCount { get; init; }
    [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
    public required long FetchedCount { get; init; }
}

public sealed record StorageDiagnostics
{
    public required DiagnosticAvailability DataVolumeAvailability { get; init; }
    public ByteCount? DataVolumeFreeBytes { get; init; }
    public required DiagnosticAvailability CasAvailability { get; init; }
    public string? Error { get; init; }
}
