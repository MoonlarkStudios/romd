using ErrorOr;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

public sealed record MediaReconciliationSelectionState(MediaReconciliationSelectionProgress Progress, bool CancelRequested);

/// <summary>Bounded durable enumeration. Subject workers own all media work and its source custody.</summary>
public interface IMediaReconciliationSelections
{
    Task<MediaReconciliationSelectionState?> ReadAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Persist current child outcomes under the active parent's fence, including while other children remain active.</summary>
    Task<ErrorOr<MediaReconciliationSelectionState>> RefreshAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Commit one bounded page, child demand, original ownership, dispatch and progress together.</summary>
    Task<ErrorOr<MediaReconciliationSelectionState>> AdvanceAsync(Guid jobId, int maximum, CancellationToken ct = default);
    /// <summary>Stop enumeration and signal only the children originally created by this selection. No sources are released here.</summary>
    Task<ErrorOr<MediaReconciliationSelectionState>> StopOwnedAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Journal only after all originally owned children have durably stopped. Completion also awaits every selected subject.</summary>
    Task<ErrorOr<MediaReconciliationJournalOutcome>> FinishAsync(Guid jobId, MediaReconciliationOutcomeKind kind, string code, CancellationToken ct = default);
}
