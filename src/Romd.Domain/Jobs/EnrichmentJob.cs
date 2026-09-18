namespace Romd.Domain.Jobs;

public sealed class EnrichmentJob : Job
{
    private EnrichmentJob()
    {
    }

    public int TitleId { get; private set; }
    public bool ArtworkOnly { get; private set; }

    public EnrichmentJobPhase PhaseEnum { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is EnrichmentJobPhase.Completed
        or EnrichmentJobPhase.Failed
        or EnrichmentJobPhase.Cancelled;

    /// <inheritdoc />
    public override double ProgressPercent => PhaseEnum switch
    {
        EnrichmentJobPhase.Pending => 0,
        EnrichmentJobPhase.Enriching => 50,
        _ => 100
    };

    public static EnrichmentJob Create(string titleName, int titleId, int? platformId, TimeProvider timeProvider, bool artworkOnly = false)
    {
        return new EnrichmentJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceFilename = titleName,
            TitleId = titleId,
            ArtworkOnly = artworkOnly,
            PlatformId = platformId,
            PhaseEnum = EnrichmentJobPhase.Pending,
            CreatedAt = timeProvider.GetUtcNow()
        };
    }

    internal static EnrichmentJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        EnrichmentJobPhase phase,
        string? hangfireJobId,
        int titleId,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId, bool artworkOnly = false)
    {
        var job = new EnrichmentJob
        {
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            HangfireJobId = hangfireJobId,
            TitleId = titleId,
            ArtworkOnly = artworkOnly,
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

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(EnrichmentJobPhase.Pending);
        PhaseEnum = EnrichmentJobPhase.Enriching;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete() => EnsurePhase(EnrichmentJobPhase.Enriching);

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = EnrichmentJobPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = EnrichmentJobPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = EnrichmentJobPhase.Cancelled;

    private void EnsurePhase(EnrichmentJobPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }
}
