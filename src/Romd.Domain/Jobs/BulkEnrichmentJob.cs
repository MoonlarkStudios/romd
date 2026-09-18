using Romd.Domain.Catalog;

namespace Romd.Domain.Jobs;

public sealed class BulkEnrichmentJob : Job
{
    private BulkEnrichmentJob()
    {
    }

    public BulkEnrichmentPhase PhaseEnum { get; private set; }

    /// <summary>
    ///     Which titles this run targets (tracked-only by default, or every title).
    /// </summary>
    public EnrichmentScope Scope { get; private set; }

    public int TotalTitles { get; private set; }
    public int ProcessedCount { get; private set; }
    public int EnrichedCount { get; private set; }
    public int NotFoundCount { get; private set; }
    public int FailedCount { get; private set; }
    public int SkippedCount { get; private set; }

    /// <inheritdoc />
    public override string Phase => PhaseEnum.ToString();

    /// <inheritdoc />
    public override bool IsTerminal => PhaseEnum is BulkEnrichmentPhase.Completed
        or BulkEnrichmentPhase.CompletedWithErrors
        or BulkEnrichmentPhase.Failed
        or BulkEnrichmentPhase.Cancelled;

    /// <inheritdoc />
    public override double ProgressPercent => PhaseEnum switch
    {
        BulkEnrichmentPhase.Pending => 0,
        BulkEnrichmentPhase.Enriching when TotalTitles == 0 => 50,
        BulkEnrichmentPhase.Enriching => 100.0 * ProcessedCount / TotalTitles,
        _ => 100
    };

    public static BulkEnrichmentJob Create(
        int platformId,
        string platformName,
        EnrichmentScope scope = EnrichmentScope.Tracked,
        Guid? createdByUserId = null)
    {
        return new BulkEnrichmentJob
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            // SourceFilename carries the job's subject — the platform's short name
            // (e.g. "SNES") — captured at creation so it survives later changes.
            SourceFilename = string.IsNullOrWhiteSpace(platformName) ? "Bulk Enrichment" : platformName,
            PlatformId = platformId,
            PhaseEnum = BulkEnrichmentPhase.Pending,
            Scope = scope,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId
        };
    }

    internal static BulkEnrichmentJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        BulkEnrichmentPhase phase,
        EnrichmentScope scope,
        string? hangfireJobId,
        int totalTitles,
        int processedCount,
        int enrichedCount,
        int notFoundCount,
        int failedCount,
        int skippedCount,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId)
    {
        var job = new BulkEnrichmentJob
        {
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = sourceFilename,
            PlatformId = platformId,
            PhaseEnum = phase,
            Scope = scope,
            HangfireJobId = hangfireJobId,
            TotalTitles = totalTitles,
            ProcessedCount = processedCount,
            EnrichedCount = enrichedCount,
            NotFoundCount = notFoundCount,
            FailedCount = failedCount,
            SkippedCount = skippedCount,
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

    public void RecordEnriched()
    {
        EnsurePhase(BulkEnrichmentPhase.Enriching);
        ProcessedCount++;
        EnrichedCount++;
    }

    public void RecordNotFound()
    {
        EnsurePhase(BulkEnrichmentPhase.Enriching);
        ProcessedCount++;
        NotFoundCount++;
    }

    public void RecordFailed(int titleId, string titleName, string error, JobErrorReason reason)
    {
        EnsurePhase(BulkEnrichmentPhase.Enriching);
        ProcessedCount++;
        FailedCount++;
        AddError(titleName, error, titleId, reason);
    }

    public void RecordSkipped()
    {
        EnsurePhase(BulkEnrichmentPhase.Enriching);
        ProcessedCount++;
        SkippedCount++;
    }

    /// <inheritdoc />
    protected override void TransitionFromPending()
    {
        EnsurePhase(BulkEnrichmentPhase.Pending);
        PhaseEnum = BulkEnrichmentPhase.Enriching;
    }

    /// <inheritdoc />
    protected override void ValidateCanComplete() => EnsurePhase(BulkEnrichmentPhase.Enriching);

    /// <inheritdoc />
    protected override void SetTerminalPhase(bool hasErrors) =>
        PhaseEnum = hasErrors ? BulkEnrichmentPhase.CompletedWithErrors : BulkEnrichmentPhase.Completed;

    /// <inheritdoc />
    protected override void SetFailedPhase() => PhaseEnum = BulkEnrichmentPhase.Failed;

    /// <inheritdoc />
    protected override void SetCancelledPhase() => PhaseEnum = BulkEnrichmentPhase.Cancelled;

    private void EnsurePhase(BulkEnrichmentPhase expected)
    {
        if (PhaseEnum != expected)
        {
            throw new InvalidOperationException(
                $"Invalid phase transition: expected {expected}, was {PhaseEnum}");
        }
    }
}
