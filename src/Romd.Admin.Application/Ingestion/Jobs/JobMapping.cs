using Romd.Application.Common.Ids;
using Romd.Application.Common.Systems;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
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
    public static Models.Job ToContract(this Job job, SystemKeys systemKeys)
    {
        return job switch
        {
            UploadJob upload => upload.ToContract(systemKeys),
            ReplaceDatJob replace => replace.ToContract(systemKeys),
            EnrichmentJob enrichment => enrichment.ToContract(systemKeys),
            ArtworkCompletionJob completion => new Models.ArtworkCompletionOperation
            {
                Id = completion.Id,
                CorrelationId = completion.CorrelationId,
                SourceFilename = completion.SourceFilename,
                Mode = completion.Mode,
                Phase = completion.Phase,
                ProgressPercent = completion.ProgressPercent,
                Total = completion.Total,
                Processed = completion.Processed,
                CurrentItem = completion.CurrentItem,
                CreatedAt = completion.CreatedAt,
                StartedAt = completion.StartedAt,
                CompletedAt = completion.CompletedAt,
                NextAttemptAt = completion.NextAttemptAt,
                IsTerminal = completion.IsTerminal,
                IsArchived = completion.IsArchived,
                HasErrors = completion.HasErrors,
                Errors = completion.Errors.Select(x => x.ToContract()).ToList()
            },
            MetadataRematerializationJob metadata => new Models.MetadataRematerializationJob
            {
                Id = metadata.Id,
                CorrelationId = metadata.CorrelationId,
                SourceFilename = metadata.SourceFilename,
                SystemKey = systemKeys.Optional(metadata.PlatformId),
                Phase = metadata.Phase,
                ProgressPercent = metadata.ProgressPercent,
                TotalTitles = metadata.TotalTitles,
                ProcessedCount = metadata.ProcessedCount,
                DeferredCount = metadata.DeferredCount,
                CurrentItem = metadata.CurrentItem,
                CreatedAt = metadata.CreatedAt,
                StartedAt = metadata.StartedAt,
                CompletedAt = metadata.CompletedAt,
                NextAttemptAt = metadata.NextAttemptAt,
                IsTerminal = metadata.IsTerminal,
                IsArchived = metadata.IsArchived,
                HasErrors = metadata.HasErrors,
                Errors = metadata.Errors.Select(x => x.ToContract()).ToList()
            },
            BulkEnrichmentJob bulk => bulk.ToContract(systemKeys),
            ExportJob export => export.ToContract(systemKeys),
            MaterializationJob materialization => materialization.ToContract(systemKeys),
            ArtworkImportJob artwork => artwork.ToContract(systemKeys),
            MediaReconciliationJob media => media.ToContract(systemKeys),
            _ => throw new InvalidOperationException($"Unknown job type: {job.GetType().Name}")
        };
    }

    /// <summary>
    ///     Maps an UploadJob to its contract representation.
    /// </summary>
    public static Models.UploadJob ToContract(this UploadJob job, SystemKeys systemKeys)
    {
        return new Models.UploadJob
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
            TrackedOnly = job.TrackedOnly,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(e => e.ToContract()).Concat(
                !job.IsTerminal && job.LastDeferralError is { } reason
                    ? [new JobError("deferral", reason, job.LastDeferralAt ?? job.CreatedAt).ToContract()] : []).ToList(),
            HasErrors = job.HasErrors,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a ReplaceDatJob to its contract representation.
    /// </summary>
    public static Models.ReplaceDatJob ToContract(this ReplaceDatJob job, SystemKeys systemKeys)
    {
        return new Models.ReplaceDatJob
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
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps an EnrichmentJob to its contract representation.
    /// </summary>
    public static Models.EnrichmentJob ToContract(this EnrichmentJob job, SystemKeys systemKeys)
    {
        return new Models.EnrichmentJob
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
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a BulkEnrichmentJob to its contract representation.
    /// </summary>
    public static Models.BulkEnrichmentJob ToContract(this BulkEnrichmentJob job, SystemKeys systemKeys)
    {
        return new Models.BulkEnrichmentJob
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
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps an ExportJob to its contract representation.
    /// </summary>
    public static Models.ExportJob ToContract(this ExportJob job, SystemKeys systemKeys)
    {
        return new Models.ExportJob
        {
            Format = Romd.Application.Common.Content.RomDeliveryFormats.ToWire(job.Format),
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
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    /// <summary>
    ///     Maps a MaterializationJob to its contract representation.
    /// </summary>
    public static Models.MaterializationJob ToContract(this MaterializationJob job, SystemKeys systemKeys)
    {
        return new Models.MaterializationJob
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
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    public static Models.ArtworkImportJob ToContract(this ArtworkImportJob job, SystemKeys systemKeys) => new()
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
        NextAttemptAt = job.NextAttemptAt,
        Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
        IsTerminal = job.IsTerminal,
        IsArchived = job.IsArchived
    };

    public static Models.MediaReconciliationJob ToContract(this MediaReconciliationJob job, SystemKeys systemKeys)
    {
        var selected = job.Request as MediaReconciliationRequest.SelectionWork;
        var subject = (job.Request as MediaReconciliationRequest.SubjectWork)?.Subject;
        var progress = job.SelectionProgress;
        var kind = selected?.Filter as MediaReconciliationFilter.KindSubjects;
        return new()
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            SystemKey = systemKeys.Optional(job.PlatformId),
            Phase = job.Phase,
            ProgressPercent = job.ProgressPercent,
            RequestMode = selected is null ? "Subject" : "Selection",
            Subject = subject is null ? null : new(subject.Kind.ToString(), IdCoder.Encode(subject.Id)),
            Selection = selected is null ? null : new()
            {
                Filter = selected.Filter switch { MediaReconciliationFilter.AllSubjects => "AllSubjects", MediaReconciliationFilter.KindSubjects => "KindSubjects", _ => "RetainedRepresentations" },
                KindId = kind?.Kind,
                SystemKey = systemKeys.Optional(kind?.PlatformId),
                Limit = selected.Limit,
                ConversionRequested = selected.ConversionRequested,
                Selected = progress!.Selected,
                Created = progress.Created,
                Adopted = progress.Adopted,
                Skipped = progress.Skipped,
                Completed = progress.Completed,
                Failed = progress.Failed,
                EnumerationComplete = progress.EnumerationComplete
            },
            OriginSelectionJobId = job.OriginSelectionJobId,
            Outcome = job.Outcome?.ToString(),
            OutcomeCode = job.OutcomeCode,
            CurrentItem = job.CurrentItem,
            Errors = job.Errors.Select(error => error.ToContract()).Concat(!job.IsTerminal && job.LastDeferralError is { } reason
                ? [new JobError("deferral", reason, job.LastDeferralAt ?? job.CreatedAt).ToContract()] : []).ToArray(),
            HasErrors = job.HasErrors || job.Outcome == MediaReconciliationOutcomeKind.Failed || progress?.Failed > 0,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            NextAttemptAt = job.NextAttemptAt,
            Duration = job.Duration?.ToString(@"hh\:mm\:ss"),
            IsTerminal = job.IsTerminal,
            IsArchived = job.IsArchived
        };
    }

    public static Models.JobError ToContract(this JobError error) =>
        new(error.Item, error.Message, error.OccurredAt, error.Reason.ToString());

}
