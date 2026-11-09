using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Base class for all job types. Uses JSON polymorphism for discriminated unions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "jobType")]
[JsonDerivedType(typeof(UploadJob), "upload")]
[JsonDerivedType(typeof(ReplaceDatJob), "replace_dat")]
[JsonDerivedType(typeof(EnrichmentJob), "enrichment")]
[JsonDerivedType(typeof(BulkEnrichmentJob), "bulk_enrichment")]
[JsonDerivedType(typeof(ArtworkCompletionOperation), "artwork-completion")]
[JsonDerivedType(typeof(MetadataRematerializationJob), "metadata-rematerialization")]
[JsonDerivedType(typeof(ExportJob), "export")]
[JsonDerivedType(typeof(MaterializationJob), "materialization")]
[JsonDerivedType(typeof(ArtworkImportJob), "artwork-import")]
[JsonDerivedType(typeof(MediaReconciliationJob), "media-reconciliation")]
public abstract record Job
{
    public required Guid Id { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string SourceFilename { get; init; }
    public string? SystemKey { get; init; }
    public required string Phase { get; init; }
    public required double ProgressPercent { get; init; }
    public string? CurrentItem { get; init; }

    // Errors
    public required IReadOnlyList<JobError> Errors { get; init; }
    public bool HasErrors { get; init; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Duration { get; init; }

    // Scheduled execution attempt of a deferred job; null when the job is not deferred.
    public DateTimeOffset? NextAttemptAt { get; init; }

    // State
    public bool IsTerminal { get; init; }
    public bool IsArchived { get; init; }
}

/// <summary>
///     An upload job for ingesting ROM archives.
/// </summary>
public sealed record UploadJob : Job
{
    // Provenance: server-local directory this job was imported from, when created via path import.
    public string? ImportSourcePath { get; init; }

    // Whether path-import source originals are deleted after durable, fully successful completion.
    public bool ImportMove { get; init; }

    // Discovery
    public int DatsDiscovered { get; init; }
    public int RomsDiscovered { get; init; }

    // Progress
    public int DatsProcessed { get; init; }
    public int DatsSucceeded { get; init; }
    public int RomsProcessed { get; init; }
    public int RomsIngested { get; init; }
    public int RomsDeduplicated { get; init; }
    public int RomsRejected { get; init; }
    public bool TrackedOnly { get; init; }
}

/// <summary>
///     A replace DAT job for updating an existing DAT file.
/// </summary>
public sealed record ReplaceDatJob : Job
{
    public required string ExistingDatId { get; init; }
    public string? NewDatId { get; init; }
}

/// <summary>
///     An enrichment job for fetching title metadata from external providers.
/// </summary>
public sealed record EnrichmentJob : Job
{
    public required string TitleId { get; init; }
}

/// <summary>
///     A bulk enrichment job for enriching all titles on a platform.
/// </summary>
public sealed record BulkEnrichmentJob : Job
{
    public int TotalTitles { get; init; }
    public int ProcessedCount { get; init; }
    public int EnrichedCount { get; init; }
    public int NotFoundCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
}





/// <summary>
///     An export job for producing a downloadable library archive.
/// </summary>
public sealed record ExportJob : Job
{
    public string Format { get; init; } = "original";
    public int TotalTitles { get; init; }
    public int ProcessedTitles { get; init; }
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public string? LibraryId { get; init; }
    public bool HasDownload { get; init; }
}

/// <summary>
///     A materialization job for pre-computing library contents.
/// </summary>
public sealed record MaterializationJob : Job
{
    public string? LibraryId { get; init; }
    public int TotalTitles { get; init; }
    public int ProcessedCount { get; init; }
    public int IncludedCount { get; init; }
    public int ExcludedCount { get; init; }
}

/// <summary>An explicit artwork import and pin request.</summary>
public sealed record ArtworkImportJob : Job
{
    public required string TitleId { get; init; }
    public required string Role { get; init; }
    public required string ProviderId { get; init; }
    public string? RetainedAssetId { get; init; }
    public bool WasSuperseded { get; init; }
}

/// <summary>
///     An error that occurred during job processing.
/// </summary>
public sealed record JobError(
    string Item,
    string Message,
    DateTimeOffset OccurredAt,
    string Reason = "Unknown");

/// <summary>
///     Result of retrying a job's failed items — the number of items re-enqueued.
/// </summary>
public sealed record RetryFailedResult(int Requeued);

public sealed record ArtworkCompletionOperation : Job
{ [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; init; } public int Total { get; init; } public int Processed { get; init; } }





/// <summary>A system metadata-policy pass; deferred titles retain internal durable retry intents.</summary>
public sealed record MetadataRematerializationJob : Job
{
    public int TotalTitles { get; init; }
    public int ProcessedCount { get; init; }
    public int DeferredCount { get; init; }
}
