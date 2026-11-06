using Romd.Domain.Source.Rom;

namespace Romd.Domain.Jobs;

public enum MediaReconciliationPhase
{
    Pending, Planning, Selecting, Waiting, Inspecting, Verifying, Encoding, Releasing, Publishing,
    Completed, CompletedWithErrors, Superseded, Failed, Cancelled
}
public enum MediaReconciliationOutcomeKind { Completed, CompletedWithErrors, Superseded, Failed, Cancelled }

/// <summary>One reconciliation family; terminal state is applied only from the fenced durable outcome journal.</summary>
public sealed class MediaReconciliationJob : Job
{
    private MediaReconciliationJob() { }
    public MediaReconciliationRequest Request { get; private set; } = null!;
    public Guid? OriginSelectionJobId { get; private set; }
    public MediaReconciliationPhase PhaseEnum { get; private set; }
    public MediaReconciliationOutcomeKind? Outcome { get; private set; }
    public string? OutcomeCode { get; private set; }
    public MediaReconciliationSelectionProgress? SelectionProgress { get; private set; }
    public override string Phase => PhaseEnum.ToString();
    public override bool IsTerminal => PhaseEnum is MediaReconciliationPhase.Completed or MediaReconciliationPhase.CompletedWithErrors
        or MediaReconciliationPhase.Superseded or MediaReconciliationPhase.Failed or MediaReconciliationPhase.Cancelled;
    public override double ProgressPercent => IsTerminal ? 100 : SelectionProgress is { } p
        ? p.Selected == 0 ? 0 : Math.Min(99, 100.0 * (p.Completed + p.Skipped) / p.Selected)
        : PhaseEnum switch
        {
            MediaReconciliationPhase.Inspecting => 10,
            MediaReconciliationPhase.Encoding => 30,
            MediaReconciliationPhase.Verifying => 50,
            MediaReconciliationPhase.Releasing => 80,
            MediaReconciliationPhase.Publishing => 90,
            _ => 0
        };

    public static MediaReconciliationJob Create(Guid id, MediaReconciliationRequest request, string filename,
        Guid? originSelectionJobId, Guid? actorId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (id == Guid.Empty || originSelectionJobId == Guid.Empty || originSelectionJobId == id || actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(filename) || filename.Length > 500 || filename.Any(char.IsControl) ||
            request is MediaReconciliationRequest.SelectionWork && originSelectionJobId is not null)
            throw new ArgumentException("Reconciliation requires its exact request, identity, origin and source label.");
        return new()
        {
            Id = id,
            CorrelationId = originSelectionJobId ?? id,
            Request = request,
            SourceFilename = filename,
            OriginSelectionJobId = originSelectionJobId,
            CreatedByUserId = actorId,
            CreatedAt = createdAt.ToUniversalTime(),
            PlatformId = request is MediaReconciliationRequest.SelectionWork { Filter: MediaReconciliationFilter.KindSubjects kind } ? kind.PlatformId : null,
            SelectionProgress = request is MediaReconciliationRequest.SelectionWork ? new(null, 0, 0, 0, 0, 0, 0, false) : null
        };
    }

    public void SetPhase(MediaReconciliationPhase phase)
    {
        if (IsTerminal || !ActivePhase(Request, phase))
            throw new InvalidOperationException("The phase must belong to this active request mode.");
        PhaseEnum = phase;
    }

    public void ApplySelectionProgress(MediaReconciliationSelectionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (IsTerminal || Request is not MediaReconciliationRequest.SelectionWork selection || SelectionProgress is not { } before ||
            progress.Cursor is { } cursor && (!selection.Bounds.Contains(cursor) ||
                selection.Filter is MediaReconciliationFilter.RetainedRepresentations && cursor is not MediaReconciliationSubject.Representation) ||
            before.Cursor is { } previous && (progress.Cursor is null || progress.Cursor.CompareTo(previous) < 0) ||
            progress.Selected < before.Selected || progress.Selected > selection.Bounds.MaximumSubjects ||
            progress.Created < before.Created || progress.Adopted < before.Adopted || progress.Skipped < before.Skipped ||
            progress.Created > selection.Limit || progress.Adopted > selection.Limit - progress.Created ||
            progress.Selected != progress.Created + progress.Adopted + progress.Skipped ||
            progress.Completed < before.Completed || progress.Failed < before.Failed || progress.Completed > progress.Created + progress.Adopted ||
            progress.Failed > progress.Completed || before.EnumerationComplete && !progress.EnumerationComplete)
            throw new InvalidOperationException("Invalid durable reconciliation selection progress.");
        SelectionProgress = progress;
        PhaseEnum = progress.EnumerationComplete ? MediaReconciliationPhase.Waiting : MediaReconciliationPhase.Selecting;
    }

    /// <summary>Apply an exact durable terminal record; never infer this from a successful media call.</summary>
    public void ApplyOutcome(MediaReconciliationOutcomeKind kind, string code, DateTimeOffset completedAt)
    {
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl) ||
            completedAt < CreatedAt || kind == MediaReconciliationOutcomeKind.CompletedWithErrors &&
                Request is not MediaReconciliationRequest.SelectionWork)
            throw new ArgumentException("A bounded authoritative outcome is required.");
        if (Outcome is not null)
        {
            if (Outcome != kind || OutcomeCode != code || CompletedAt != completedAt)
                throw new InvalidOperationException("The durable reconciliation outcome cannot change.");
            return;
        }
        if (kind is MediaReconciliationOutcomeKind.Completed or MediaReconciliationOutcomeKind.CompletedWithErrors &&
            SelectionProgress is { } p && (!p.EnumerationComplete || p.Completed + p.Skipped != p.Selected ||
                (kind == MediaReconciliationOutcomeKind.CompletedWithErrors) != (p.Failed > 0)))
            throw new InvalidOperationException("Selection completion requires all selected work and its actual failure count.");
        Outcome = kind;
        OutcomeCode = code;
        CompletedAt = completedAt.ToUniversalTime();
        CurrentItem = null;
        PhaseEnum = TerminalPhase(kind);
    }

    internal static MediaReconciliationJob Rehydrate(Guid id, Guid correlationId, MediaReconciliationRequest request,
        string filename, Guid? originSelectionJobId, Guid? actorId, DateTimeOffset createdAt, int? platformId, MediaReconciliationPhase phase,
        MediaReconciliationOutcomeKind? outcome, string? outcomeCode, MediaReconciliationSelectionProgress? progress,
        string? hangfireJobId, string? currentItem, IReadOnlyList<JobError> errors, DateTimeOffset? startedAt,
        DateTimeOffset? completedAt, bool isArchived, DateTimeOffset? archivedAt)
    {
        if (correlationId == Guid.Empty || !Enum.IsDefined(phase) || (outcome is null) != (completedAt is null) ||
            (outcome is null) != (outcomeCode is null) || (request is MediaReconciliationRequest.SelectionWork) != (progress is not null) ||
            outcome is null && phase != MediaReconciliationPhase.Pending && !ActivePhase(request, phase) ||
            outcome is not null && phase != TerminalPhase(outcome.Value))
            throw new ArgumentException("Persisted reconciliation state must match its request and durable outcome.");
        var job = Create(id, request, filename, originSelectionJobId, actorId, createdAt);
        if (progress is not null)
            job.ApplySelectionProgress(progress);
        job.PlatformId = platformId;
        job.CorrelationId = correlationId;
        job.HangfireJobId = hangfireJobId;
        job.CurrentItem = currentItem;
        job.StartedAt = startedAt;
        job.PhaseEnum = phase;
        job.IsArchived = isArchived;
        job.ArchivedAt = archivedAt;
        job._errors.AddRange(errors);
        if (outcome is not null)
            job.ApplyOutcome(outcome.Value, outcomeCode!, completedAt!.Value);
        return job;
    }

    protected override void TransitionFromPending()
    {
        if (PhaseEnum != MediaReconciliationPhase.Pending)
            throw new InvalidOperationException("Reconciliation has already started.");
        PhaseEnum = Request is MediaReconciliationRequest.SelectionWork ? MediaReconciliationPhase.Selecting : MediaReconciliationPhase.Planning;
    }
    private static bool ActivePhase(MediaReconciliationRequest request, MediaReconciliationPhase phase) => request is MediaReconciliationRequest.SelectionWork
        ? phase is MediaReconciliationPhase.Selecting or MediaReconciliationPhase.Waiting
        : phase is MediaReconciliationPhase.Planning or MediaReconciliationPhase.Inspecting or MediaReconciliationPhase.Verifying or
            MediaReconciliationPhase.Encoding or MediaReconciliationPhase.Releasing or MediaReconciliationPhase.Publishing;
    private static MediaReconciliationPhase TerminalPhase(MediaReconciliationOutcomeKind outcome) => outcome switch
    {
        MediaReconciliationOutcomeKind.Completed => MediaReconciliationPhase.Completed,
        MediaReconciliationOutcomeKind.CompletedWithErrors => MediaReconciliationPhase.CompletedWithErrors,
        MediaReconciliationOutcomeKind.Superseded => MediaReconciliationPhase.Superseded,
        MediaReconciliationOutcomeKind.Failed => MediaReconciliationPhase.Failed,
        MediaReconciliationOutcomeKind.Cancelled => MediaReconciliationPhase.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
}
