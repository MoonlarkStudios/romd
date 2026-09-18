namespace Romd.Domain.Jobs;

public sealed class ExportJob : Job
{
    private ExportJob()
    {
    }

    public ExportPhase PhaseEnum { get; private set; }

    public ExportScopeKind? ScopeKind { get; private set; }
    public int? LibraryId { get; private set; }
    public long? AuthorizedMaterializationGeneration { get; private set; }
    public int TotalTitles { get; private set; }
    public int ProcessedTitles { get; private set; }
    public int TotalFiles { get; private set; }
    public int ProcessedFiles { get; private set; }
    public int SkippedFiles { get; private set; }
    public string? ExportPath { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is ExportPhase.Completed
        or ExportPhase.CompletedWithErrors
        or ExportPhase.Failed
        or ExportPhase.Cancelled;

    /// <inheritdoc />
    public override double ProgressPercent => PhaseEnum switch
    {
        ExportPhase.Pending => 0,
        ExportPhase.Collecting => 5,
        ExportPhase.Packaging when TotalFiles == 0 => 50,
        ExportPhase.Packaging => 5 + 85.0 * ProcessedFiles / TotalFiles,
        ExportPhase.Storing => 95,
        _ => 100
    };

    public static ExportJob CreateLibrary(
        int libraryId,
        long authorizedMaterializationGeneration,
        Guid? createdByUserId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(libraryId);
        ArgumentOutOfRangeException.ThrowIfNegative(authorizedMaterializationGeneration);

        return new ExportJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceFilename = "Library Export",
            ScopeKind = ExportScopeKind.Library,
            LibraryId = libraryId,
            AuthorizedMaterializationGeneration = authorizedMaterializationGeneration,
            PhaseEnum = ExportPhase.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    public static ExportJob CreateAllCatalog(Guid? createdByUserId = null)
    {
        return new ExportJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceFilename = "Library Export",
            ScopeKind = ExportScopeKind.AllCatalog,
            PhaseEnum = ExportPhase.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    internal static ExportJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        ExportPhase phase,
        string? hangfireJobId,
        ExportScopeKind? scopeKind,
        int? libraryId,
        long? authorizedMaterializationGeneration,
        int totalTitles,
        int processedTitles,
        int totalFiles,
        int processedFiles,
        int skippedFiles,
        string? exportPath,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId)
    {
        var job = new ExportJob
        {
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            HangfireJobId = hangfireJobId,
            ScopeKind = scopeKind,
            LibraryId = libraryId,
            AuthorizedMaterializationGeneration = authorizedMaterializationGeneration,
            TotalTitles = totalTitles,
            ProcessedTitles = processedTitles,
            TotalFiles = totalFiles,
            ProcessedFiles = processedFiles,
            SkippedFiles = skippedFiles,
            ExportPath = exportPath,
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

    public void BeginCollecting()
    {
        EnsurePhase(ExportPhase.Collecting);
    }

    public void BeginPackaging()
    {
        EnsurePhase(ExportPhase.Collecting);
        PhaseEnum = ExportPhase.Packaging;
    }

    public void BeginStoring()
    {
        EnsurePhase(ExportPhase.Packaging);
        PhaseEnum = ExportPhase.Storing;
    }

    public void SetTotals(int totalTitles, int totalFiles)
    {
        TotalTitles = totalTitles;
        TotalFiles = totalFiles;
    }

    public void RecordTitleProcessed()
    {
        EnsurePhase(ExportPhase.Packaging);
        ProcessedTitles++;
    }

    public void RecordFileProcessed()
    {
        EnsurePhase(ExportPhase.Packaging);
        ProcessedFiles++;
    }

    public void RecordFileSkipped()
    {
        EnsurePhase(ExportPhase.Packaging);
        ProcessedFiles++;
        SkippedFiles++;
    }

    public void SetExportPath(string path)
    {
        ExportPath = path;
    }

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(ExportPhase.Pending);
        PhaseEnum = ExportPhase.Collecting;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete()
    {
        if (PhaseEnum is not (ExportPhase.Collecting or ExportPhase.Packaging or ExportPhase.Storing))
        {
            throw new InvalidOperationException(
                $"Cannot complete export job in phase {PhaseEnum}");
        }
    }

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = hasErrors ? ExportPhase.CompletedWithErrors : ExportPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = ExportPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = ExportPhase.Cancelled;

    private void EnsurePhase(ExportPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }
}
