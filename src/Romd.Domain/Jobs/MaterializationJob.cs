namespace Romd.Domain.Jobs;

public sealed class MaterializationJob : Job
{
    private bool _isDeferred;

    private MaterializationJob()
    {
    }

    public MaterializationPhase PhaseEnum { get; private set; }

    public int LibraryId { get; private set; }
    public int TotalTitles { get; private set; }
    public int ProcessedCount { get; private set; }
    public int IncludedCount { get; private set; }
    public int ExcludedCount { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is MaterializationPhase.Completed
        or MaterializationPhase.CompletedWithErrors
        or MaterializationPhase.Failed
        or MaterializationPhase.Cancelled
        or MaterializationPhase.Deferred;

    /// <inheritdoc />
    public override double ProgressPercent => PhaseEnum switch
    {
        MaterializationPhase.Pending => 0,
        MaterializationPhase.Materializing when TotalTitles == 0 => 50,
        MaterializationPhase.Materializing => 100.0 * ProcessedCount / TotalTitles,
        _ => 100
    };

    public static MaterializationJob Create(int libraryId, string libraryName, Guid? createdByUserId = null)
    {
        return new MaterializationJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            // SourceFilename carries the job's subject; for materialization that's
            // the library name, captured here so the record stays accurate even if
            // the library is later renamed or deleted.
            SourceFilename = string.IsNullOrWhiteSpace(libraryName) ? "Library Materialization" : libraryName,
            LibraryId = libraryId,
            PhaseEnum = MaterializationPhase.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    internal static MaterializationJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        MaterializationPhase phase,
        string? hangfireJobId,
        int libraryId,
        int totalTitles,
        int processedCount,
        int includedCount,
        int excludedCount,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId)
    {
        var job = new MaterializationJob
        {
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            HangfireJobId = hangfireJobId,
            LibraryId = libraryId,
            TotalTitles = totalTitles,
            ProcessedCount = processedCount,
            IncludedCount = includedCount,
            ExcludedCount = excludedCount,
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

    public void SetTotalTitles(int total) => TotalTitles = total;

    public void RecordIncluded()
    {
        EnsurePhase(MaterializationPhase.Materializing);
        ProcessedCount++;
        IncludedCount++;
    }

    public void RecordExcluded()
    {
        EnsurePhase(MaterializationPhase.Materializing);
        ProcessedCount++;
        ExcludedCount++;
    }

    /// <summary>
    ///     Marks the run as deferred by the catalog gate so that <see cref="Job.Complete" />
    ///     lands in <see cref="MaterializationPhase.Deferred" /> instead of Completed.
    ///     The persisted phase string is the durable record; this flag only spans the run.
    /// </summary>
    public void MarkDeferred()
    {
        EnsurePhase(MaterializationPhase.Materializing);
        _isDeferred = true;
    }

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(MaterializationPhase.Pending);
        PhaseEnum = MaterializationPhase.Materializing;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete() => EnsurePhase(MaterializationPhase.Materializing);

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = _isDeferred
            ? MaterializationPhase.Deferred
            : hasErrors
                ? MaterializationPhase.CompletedWithErrors
                : MaterializationPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = MaterializationPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = MaterializationPhase.Cancelled;

    private void EnsurePhase(MaterializationPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }
}
