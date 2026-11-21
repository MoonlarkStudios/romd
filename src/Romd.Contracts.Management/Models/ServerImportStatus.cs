namespace Romd.Contracts.Management.Models;

/// <summary>The configured server drop folder and its most recent check and import.</summary>
public sealed record ServerImportStatus(
    string? Directory, int? IntervalMinutes, DateTimeOffset? NextCheckAt,
    DateTimeOffset? LastCheckedAt, string? LastCheckOutcome, int WaitingFiles, string? Message,
    Guid? LastJobId, string? LastJobPhase, int LastQueuedFiles, bool IsRunning, bool CanRetry);
