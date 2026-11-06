namespace Romd.Domain.Jobs;

/// <summary>A resumable pass through a system's titles using current metadata defaults.</summary>
public sealed class MetadataRematerializationJob : DirectLifecycleJob
{
    private string _phase = "Pending";
    public override string Phase => _phase;
    public int TotalTitles { get; private set; }
    public int ProcessedCount { get; private set; }
    public int DeferredCount { get; private set; }
    public int CursorTitleId { get; private set; }
    public override bool IsTerminal => _phase is "Completed" or "CompletedWithErrors" or "Failed";
    public override double ProgressPercent => TotalTitles == 0 ? (IsTerminal ? 100 : 0) : Math.Min(100, 100d * ProcessedCount / TotalTitles);

    public static MetadataRematerializationJob Create(int platformId, string shortName, Guid? actor, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        PlatformId = platformId,
        SourceFilename = shortName,
        CreatedByUserId = actor,
        CreatedAt = createdAt
    };
    public void SetTotalTitles(int total) => TotalTitles = Math.Max(total, ProcessedCount);
    public void RecordProcessed(int titleId, DateTimeOffset occurredAt, string? error = null)
    {
        if (titleId <= CursorTitleId)
            throw new InvalidOperationException("Metadata cursor must advance.");
        CursorTitleId = titleId;
        ProcessedCount++;
        if (error is not null)
        { DeferredCount++; RecordError(titleId.ToString(System.Globalization.CultureInfo.InvariantCulture), error, occurredAt); }
    }
    internal static MetadataRematerializationJob Restore(Guid id, Guid correlationId, int? platformId, string source,
        int total, int processed, int deferred, int cursor, string phase, string? hangfireId,
        DateTimeOffset created, DateTimeOffset? started, DateTimeOffset? completed, bool archived,
        DateTimeOffset? archivedAt, Guid? actor, string? current, IReadOnlyList<JobError> errors)
    {
        var job = new MetadataRematerializationJob
        {
            Id = id,
            CorrelationId = correlationId,
            PlatformId = platformId,
            SourceFilename = source,
            TotalTitles = total,
            ProcessedCount = processed,
            DeferredCount = deferred,
            CursorTitleId = cursor,
            _phase = phase,
            HangfireJobId = hangfireId,
            CreatedAt = created,
            StartedAt = started,
            CompletedAt = completed,
            IsArchived = archived,
            ArchivedAt = archivedAt,
            CreatedByUserId = actor,
            CurrentItem = current
        };
        job._errors.AddRange(errors);
        return job;
    }
    protected override void TransitionFromPending() => _phase = "Rematerializing";
    protected override void SetTerminalPhase(bool hasErrors) => _phase = DeferredCount > 0 || hasErrors ? "CompletedWithErrors" : "Completed";
    protected override void SetFailedPhase() => _phase = "Failed";
}
