using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Domain.Jobs;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Extension methods for mapping job domain entities to contract models.
/// </summary>
public static class JobMapping
{
    /// <summary>
    ///     Maps any Job subtype to its contract representation.
    /// </summary>
    public static Models.JobDto ToContract(this Job job, SystemKeys systemKeys)
    {
        return job switch
        {
            UploadJob upload => upload.ToContract(systemKeys),
            ReplaceDatJob replace => replace.ToContract(systemKeys),
            EnrichmentJob enrichment => enrichment.ToContract(systemKeys),
            BulkEnrichmentJob bulk => bulk.ToContract(systemKeys),
            ExportJob export => export.ToContract(systemKeys),
            MaterializationJob materialization => materialization.ToContract(systemKeys),
            ArtworkImportJob artwork => artwork.ToContract(systemKeys),
            _ => throw new InvalidOperationException($"Unknown job type: {job.GetType().Name}")
        };
    }

    /// <summary>
    ///     Maps an UploadJob to its contract representation.
    /// </summary>
    public static Models.UploadJobDto ToContract(this UploadJob job, SystemKeys systemKeys)
    {
        return new Models.UploadJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            ImportSourcePath = job.ImportSourcePath,
            ImportMove = job.ImportMove,
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            DatsDiscovered = job.DatsDiscovered,
            RomsDiscovered = job.RomsDiscovered,
            DatsProcessed = job.DatsProcessed,
            DatsSucceeded = job.DatsSucceeded,
            RomsProcessed = job.RomsProcessed,
            RomsIngested = job.RomsIngested,
            RomsDeduplicated = job.RomsDeduplicated,
            RomsRejected = job.RomsRejected,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a ReplaceDatJob to its contract representation.
    /// </summary>
    public static Models.ReplaceDatJobDto ToContract(this ReplaceDatJob job, SystemKeys systemKeys)
    {
        return new Models.ReplaceDatJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            CurrentItem = job.CurrentItem,
            ExistingDatId = IdCoder.Encode(job.ExistingDatId),
            NewDatId = job.NewDatId.HasValue ? IdCoder.Encode(job.NewDatId.Value) : null,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps an EnrichmentJob to its contract representation.
    /// </summary>
    public static Models.EnrichmentJobDto ToContract(this EnrichmentJob job, SystemKeys systemKeys)
    {
        return new Models.EnrichmentJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            TitleId = IdCoder.Encode(job.TitleId),
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a BulkEnrichmentJob to its contract representation.
    /// </summary>
    public static Models.BulkEnrichmentJobDto ToContract(this BulkEnrichmentJob job, SystemKeys systemKeys)
    {
        return new Models.BulkEnrichmentJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            TotalTitles = job.TotalTitles,
            ProcessedCount = job.ProcessedCount,
            EnrichedCount = job.EnrichedCount,
            NotFoundCount = job.NotFoundCount,
            FailedCount = job.FailedCount,
            SkippedCount = job.SkippedCount,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps an ExportJob to its contract representation.
    /// </summary>
    public static Models.ExportJobDto ToContract(this ExportJob job, SystemKeys systemKeys)
    {
        return new Models.ExportJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            TotalTitles = job.TotalTitles,
            ProcessedTitles = job.ProcessedTitles,
            TotalFiles = job.TotalFiles,
            ProcessedFiles = job.ProcessedFiles,
            SkippedFiles = job.SkippedFiles,
            LibraryId = job.LibraryId.HasValue ? IdCoder.Encode(job.LibraryId.Value) : null,
            HasDownload = job.IsTerminal && job.ExportPath is not null,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a MaterializationJob to its contract representation.
    /// </summary>
    public static Models.MaterializationJobDto ToContract(this MaterializationJob job, SystemKeys systemKeys)
    {
        return new Models.MaterializationJobDto
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            LibraryId = IdCoder.Encode(job.LibraryId),
            TotalTitles = job.TotalTitles,
            ProcessedCount = job.ProcessedCount,
            IncludedCount = job.IncludedCount,
            ExcludedCount = job.ExcludedCount,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    public static Models.ArtworkImportJobDto ToContract(this ArtworkImportJob job, SystemKeys systemKeys) => new()
    {
        Id = job.Id,
        CorrelationId = job.CorrelationId,
        SourceFilename = job.SourceFilename,
        SystemKey = systemKeys.Optional(job.PlatformId),
        Phase = job.Phase,
        ProgressPercent = job.ProgressPercent,
        TitleId = IdCoder.Encode(job.TitleId),
        Role = job.Role.ToString(),
        ProviderId = job.ProviderId,
        RetainedAssetId = job.RetainedAssetId.HasValue ? IdCoder.Encode(job.RetainedAssetId.Value) : null,
        WasSuperseded = job.WasSuperseded,
        CurrentItem = job.CurrentItem,
        Errors = job.Errors.Select(error => error.ToContract()).ToList(),
        HasErrors = job.HasErrors,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
        IsTerminal = job.IsTerminal,
        IsArchived = job.IsArchived
    };

    /// <summary>
    ///     Maps a JobError to its contract representation.
    /// </summary>
    public static Models.JobError ToContract(this JobError error) =>
        new(error.Item, error.Message, error.OccurredAt, error.Reason.ToString());

}
