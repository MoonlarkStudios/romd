namespace Romd.Domain.Jobs;

/// <summary>
///     Represents an upload job that processes files (DATs and ROMs).
///     This is a state machine that tracks progress through distinct phases.
/// </summary>
public sealed class UploadJob : Job
{
    /// <summary>
    ///     EF Core constructor.
    /// </summary>
    private UploadJob()
    {
    }

    /// <summary>
    ///     Creates a new upload job.
    /// </summary>
    public static UploadJob Create(string sourceFilename, int? platformId = null, Guid? createdByUserId = null,
        Guid? requestId = null, Guid? batchId = null)
    {
        return new UploadJob
        {
            Id = requestId ?? Guid.NewGuid(),
            CorrelationId = batchId ?? Guid.NewGuid(),
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = UploadPhase.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    /// <summary>
    ///     Rehydrates an upload job from persistence. Trusts that data is valid.
    /// </summary>
    internal static UploadJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        UploadPhase phase,
        string? hangfireJobId,
        int datsDiscovered,
        int romsDiscovered,
        int datsProcessed,
        int datsSucceeded,
        int romsProcessed,
        int romsIngested,
        int romsDeduplicated,
        int romsRejected,
        int maxParallelRoms,
        bool allowUnidentified,
        bool archiveOnly,
        string? importSourcePath,
        bool importMove,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId)
    {
        var job = new UploadJob
        {
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            HangfireJobId = hangfireJobId,
            DatsDiscovered = datsDiscovered,
            RomsDiscovered = romsDiscovered,
            DatsProcessed = datsProcessed,
            DatsSucceeded = datsSucceeded,
            RomsProcessed = romsProcessed,
            RomsIngested = romsIngested,
            RomsDeduplicated = romsDeduplicated,
            RomsRejected = romsRejected,
            MaxParallelRoms = maxParallelRoms,
            AllowUnidentified = allowUnidentified,
            ArchiveOnly = archiveOnly,
            ImportSourcePath = importSourcePath,
            ImportMove = importMove,
            CurrentItem = currentItem,
            CreatedAt = createdAt,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            IsArchived = isArchived,
            ArchivedAt = archivedAt,
            CreatedByUserId = createdByUserId
        };
        job._errors.AddRange(errors);
        return job;
    }

    /// <summary>
    ///     Current phase of the job.
    /// </summary>
    public UploadPhase PhaseEnum { get; private set; }

    /// <summary>
    ///     Maximum number of ROMs to process in parallel.
    /// </summary>
    public int MaxParallelRoms { get; private set; } = 4;

    /// <summary>
    ///     Whether ROMs that match no DAT entry are stored as unidentified (true) or rejected and
    ///     discarded (false, default). The executor additionally gates this on the creator's role.
    /// </summary>
    public bool AllowUnidentified { get; private set; }

    /// <summary>
    ///     Whether matched ROM titles remain outside the tracked collection. Files are still
    ///     ingested and playable when this is true.
    /// </summary>
    public bool ArchiveOnly { get; private set; }

    /// <summary>
    ///     Server-local directory this job was imported from, when created via path import.
    /// </summary>
    public string? ImportSourcePath { get; private set; }

    /// <summary>
    ///     Whether path-import source originals should be deleted after a fully successful
    ///     completion. Population always copies; sources are never at risk before then.
    /// </summary>
    public bool ImportMove { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <summary>
    ///     Number of DAT files discovered during classification.
    /// </summary>
    public int DatsDiscovered { get; private set; }

    /// <summary>
    ///     Number of ROM files discovered during classification.
    /// </summary>
    public int RomsDiscovered { get; private set; }

    /// <summary>
    ///     Number of DAT files processed (attempted).
    /// </summary>
    public int DatsProcessed { get; private set; }

    /// <summary>
    ///     Number of DAT files successfully imported.
    /// </summary>
    public int DatsSucceeded { get; private set; }

    /// <summary>
    ///     Number of ROM files processed (attempted).
    /// </summary>
    public int RomsProcessed { get; private set; }

    /// <summary>
    ///     Number of ROM files successfully ingested.
    /// </summary>
    public int RomsIngested { get; private set; }

    /// <summary>
    ///     Number of ROM files deduplicated (already existed).
    /// </summary>
    public int RomsDeduplicated { get; private set; }

    /// <summary>
    ///     Number of ROM files rejected (unidentified).
    /// </summary>
    public int RomsRejected { get; private set; }

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is UploadPhase.Completed
        or UploadPhase.CompletedWithErrors
        or UploadPhase.Failed
        or UploadPhase.Cancelled;

    /// <inheritdoc />
    public override double ProgressPercent => CalculateProgress();

    /// <summary>
    ///     Sets the maximum number of ROMs to process in parallel.
    /// </summary>
    public void SetMaxParallelRoms(int value) => MaxParallelRoms = value;

    /// <summary>
    ///     Sets whether unmatched ROMs are stored as unidentified rather than rejected.
    /// </summary>
    public void SetAllowUnidentified(bool value) => AllowUnidentified = value;

    /// <summary>Sets whether this upload should avoid creating tracked-title intent.</summary>
    public void SetArchiveOnly(bool value) => ArchiveOnly = value;

    /// <summary>Records the server-local import source and whether originals move on success.</summary>
    public void SetImportSource(string sourcePath, bool move)
    {
        ImportSourcePath = sourcePath;
        ImportMove = move;
    }

    /// <summary>
    ///     Transitions to classification phase.
    /// </summary>
    public void BeginClassification()
    {
        EnsurePhase(UploadPhase.Extracting);
        PhaseEnum = UploadPhase.Classifying;
    }

    /// <summary>
    ///     Records the number of discovered files after classification.
    /// </summary>
    public void SetDiscoveredFiles(int dats, int roms)
    {
        EnsurePhase(UploadPhase.Classifying);
        DatsDiscovered = dats;
        RomsDiscovered = roms;
    }

    /// <summary>
    ///     Transitions to DAT ingestion phase.
    /// </summary>
    public void BeginDatIngestion()
    {
        EnsurePhase(UploadPhase.Classifying);
        PhaseEnum = UploadPhase.IngestingDats;
    }

    /// <summary>
    ///     Records the result of processing a DAT file.
    /// </summary>
    public void RecordDatResult(bool success, string? error = null)
    {
        EnsurePhase(UploadPhase.IngestingDats);
        DatsProcessed++;

        if (success)
        {
            DatsSucceeded++;
        }
        else if (error is not null)
        {
            AddError(CurrentItem ?? "unknown", error);
        }
    }

    /// <summary>
    ///     Transitions to ROM ingestion phase.
    /// </summary>
    public void BeginRomIngestion()
    {
        EnsurePhase(UploadPhase.IngestingDats);
        PhaseEnum = UploadPhase.IngestingRoms;
        CurrentItem = null;
    }

    /// <summary>
    ///     Records the result of processing a ROM file.
    /// </summary>
    public void RecordRomResult(RomIngestOutcome outcome, string? error = null)
    {
        EnsurePhase(UploadPhase.IngestingRoms);
        RomsProcessed++;

        switch (outcome)
        {
            case RomIngestOutcome.Ingested:
                RomsIngested++;
                break;
            case RomIngestOutcome.Deduplicated:
                RomsDeduplicated++;
                break;
            case RomIngestOutcome.Rejected:
                RomsRejected++;
                break;
            case RomIngestOutcome.Failed:
                if (error is not null)
                {
                    AddError(CurrentItem ?? "unknown", error);
                }

                break;
        }
    }

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(UploadPhase.Pending);
        PhaseEnum = UploadPhase.Extracting;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete() => EnsurePhase(UploadPhase.IngestingRoms);

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = hasErrors ? UploadPhase.CompletedWithErrors : UploadPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = UploadPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = UploadPhase.Cancelled;

    private void EnsurePhase(UploadPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }

    private double CalculateProgress()
    {
        return PhaseEnum switch
        {
            UploadPhase.Pending => 0,
            UploadPhase.Extracting => 5,
            UploadPhase.Classifying => 10,
            UploadPhase.IngestingDats when DatsDiscovered == 0 => 20,
            UploadPhase.IngestingDats => 20 + 30.0 * DatsProcessed / DatsDiscovered,
            UploadPhase.IngestingRoms when RomsDiscovered == 0 => 50,
            UploadPhase.IngestingRoms => 50 + 50.0 * RomsProcessed / RomsDiscovered,
            _ => 100
        };
    }
}
