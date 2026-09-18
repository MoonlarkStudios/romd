namespace Romd.Domain.Jobs;

/// <summary>
///     Represents a DAT replacement job that replaces an existing DAT with a new one.
///     This is a state machine that tracks progress through distinct phases.
/// </summary>
public sealed class ReplaceDatJob : Job
{
    /// <summary>
    ///     EF Core constructor.
    /// </summary>
    private ReplaceDatJob()
    {
    }

    /// <summary>
    ///     ID of the existing DAT to replace.
    /// </summary>
    public int ExistingDatId { get; private set; }

    /// <summary>
    ///     ID of the newly ingested DAT (set after successful ingestion).
    /// </summary>
    public int? NewDatId { get; private set; }

    /// <summary>
    ///     Current phase of the job.
    /// </summary>
    public ReplaceDatPhase PhaseEnum { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is ReplaceDatPhase.Completed
        or ReplaceDatPhase.CompletedWithErrors
        or ReplaceDatPhase.Failed
        or ReplaceDatPhase.Cancelled;

    /// <inheritdoc />
    public override double ProgressPercent => PhaseEnum switch
    {
        ReplaceDatPhase.Pending => 0,
        ReplaceDatPhase.Ingesting => 25,
        ReplaceDatPhase.Replacing => 75,
        _ => 100
    };

    /// <summary>
    ///     Creates a new DAT replacement job.
    /// </summary>
    public static ReplaceDatJob Create(int existingDatId, string filename, int? platformId = null, Guid? createdByUserId = null)
    {
        return new ReplaceDatJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ExistingDatId = existingDatId,
            SourceFilename = filename,
            PlatformId = platformId,
            PhaseEnum = ReplaceDatPhase.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    /// <summary>
    ///     Rehydrates a DAT replacement job from persistence. Trusts that data is valid.
    /// </summary>
    internal static ReplaceDatJob Rehydrate(
        Guid id,
        Guid correlationId,
        int existingDatId,
        int? newDatId,
        string sourceFilename,
        int? platformId,
        ReplaceDatPhase phase,
        string? hangfireJobId,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId)
    {
        var job = new ReplaceDatJob
        {
            Id = id,
            CorrelationId = correlationId,
            ExistingDatId = existingDatId,
            NewDatId = newDatId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            HangfireJobId = hangfireJobId,
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
    ///     Sets the ID of the newly ingested DAT.
    /// </summary>
    public void SetNewDatId(int id)
    {
        EnsurePhase(ReplaceDatPhase.Ingesting);
        NewDatId = id;
    }

    /// <summary>
    ///     Transitions to replacing phase.
    /// </summary>
    public void BeginReplacing()
    {
        EnsurePhase(ReplaceDatPhase.Ingesting);
        PhaseEnum = ReplaceDatPhase.Replacing;
    }

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(ReplaceDatPhase.Pending);
        PhaseEnum = ReplaceDatPhase.Ingesting;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete() => EnsurePhase(ReplaceDatPhase.Replacing);

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = hasErrors ? ReplaceDatPhase.CompletedWithErrors : ReplaceDatPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = ReplaceDatPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = ReplaceDatPhase.Cancelled;

    private void EnsurePhase(ReplaceDatPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }
}
