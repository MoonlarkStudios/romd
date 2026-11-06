namespace Romd.Domain.Jobs;

/// <summary>Durable catalog operation. Work and per-role results live in its item ledger.</summary>
public sealed class ArtworkCompletionJob : CancellableJob
{
    private string _phase = "Pending";
    public override string Phase => _phase;
    public string? Mode { get; private set; }
    public int Total { get; private set; }
    public int Processed { get; private set; }
    public override bool IsTerminal => _phase is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled";
    public override double ProgressPercent => Total == 0 ? (IsTerminal ? 100 : 0) : 100d * Processed / Total;
    public static ArtworkCompletionJob Create(Guid id, int total, Guid? actor, DateTimeOffset createdAt, string? mode = null) => new()
    { Id = id, CorrelationId = id, Mode = mode, SourceFilename = "Fill missing artwork", Total = total, CreatedByUserId = actor, CreatedAt = createdAt };
    public void SetProgress(int processed) => Processed = Math.Clamp(processed, 0, Total);
    internal static ArtworkCompletionJob Restore(Guid id, Guid correlationId, int total, int processed, string phase,
        string? hangfireId, DateTimeOffset created, DateTimeOffset? started, DateTimeOffset? completed,
        bool archived, DateTimeOffset? archivedAt, Guid? actor, string? current, IReadOnlyList<JobError> errors, string? mode)
    {
        var job = new ArtworkCompletionJob
        {
            Mode = mode,
            Id = id,
            CorrelationId = correlationId,
            SourceFilename = "Fill missing artwork",
            Total = total,
            Processed = processed,
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
    protected override void TransitionFromPending() => _phase = "Enriching";
    protected override void ValidateCanComplete()
    { if (Processed != Total) throw new InvalidOperationException("Every artwork item must have a durable result before completion."); }
    protected override void SetTerminalPhase(bool hasErrors) => _phase = hasErrors ? "CompletedWithErrors" : "Completed";
    protected override void SetFailedPhase() => _phase = "Failed";
    protected override void SetCancelledPhase() => _phase = "Cancelled";
}
