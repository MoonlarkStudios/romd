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

public sealed record OperationalDiagnosticsDto
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required CatalogProjectionDiagnosticsDto CatalogProjections { get; init; }
    public required OperationalJobDiagnosticsDto Jobs { get; init; }
    public required OutboxDiagnosticsDto Outbox { get; init; }
    public required HangfireDiagnosticsDto Hangfire { get; init; }
    public required StorageDiagnosticsDto Storage { get; init; }
}

public sealed record CatalogProjectionDiagnosticsDto
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<CatalogProjectionDiagnosticDto> Items { get; init; }
    public required bool IsTruncated { get; init; }
}

public sealed record CatalogProjectionDiagnosticDto
{
    public required string SystemKey { get; init; }
    public required string PlatformName { get; init; }
    public required string PlatformShortName { get; init; }
    public required CatalogProjectionState State { get; init; }
    public DateTimeOffset? RebuiltAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}

public sealed record OperationalJobDiagnosticsDto
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<WedgedReplaceDatJobDto> WedgedReplaceDatJobs { get; init; }
    public required bool WedgedReplaceDatJobsTruncated { get; init; }
    public required IReadOnlyList<StrandedBulkEnrichmentJobDto> StrandedBulkEnrichmentJobs { get; init; }
    public required bool StrandedBulkEnrichmentJobsTruncated { get; init; }
}

public sealed record WedgedReplaceDatJobDto
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

public sealed record StrandedBulkEnrichmentJobDto
{
    public required Guid JobId { get; init; }
    public string? SystemKey { get; init; }
    public required string PlatformLabel { get; init; }
    public required BulkEnrichmentDiagnosticPhase Phase { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long StrandedForSeconds { get; init; }
}

public sealed record OutboxDiagnosticsDto
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required int PendingCount { get; init; }
    public long? OldestPendingAgeSeconds { get; init; }
    public required int FailingCount { get; init; }
    public DateTimeOffset? LastProcessedAt { get; init; }
}

public sealed record HangfireDiagnosticsDto
{
    public required DiagnosticAvailability Availability { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<HangfireServerDiagnosticDto> Servers { get; init; }
    public required bool ServersTruncated { get; init; }
    public required IReadOnlyList<RecurringJobDiagnosticDto> RecurringJobs { get; init; }
    public required bool RecurringJobsTruncated { get; init; }
    public required IReadOnlyList<QueueBacklogDiagnosticDto> QueueBacklogs { get; init; }
}

public sealed record HangfireServerDiagnosticDto
{
    public required string Name { get; init; }
    public required int WorkerCount { get; init; }
    public required IReadOnlyList<string> Queues { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public long? HeartbeatAgeSeconds { get; init; }
}

public sealed record RecurringJobDiagnosticDto
{
    public required string Id { get; init; }
    public required string Queue { get; init; }
    public required string Cron { get; init; }
    public DateTimeOffset? LastExecutionAt { get; init; }
    public DateTimeOffset? NextExecutionAt { get; init; }
    public RecurringJobDiagnosticState? LastJobState { get; init; }
    public string? Error { get; init; }
}

public sealed record QueueBacklogDiagnosticDto
{
    public required string Queue { get; init; }
    [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
    public required long EnqueuedCount { get; init; }
    [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
    public required long FetchedCount { get; init; }
}

public sealed record StorageDiagnosticsDto
{
    public required DiagnosticAvailability DataVolumeAvailability { get; init; }
    public ByteCount? DataVolumeFreeBytes { get; init; }
    public required DiagnosticAvailability CasAvailability { get; init; }
    public string? Error { get; init; }
}
