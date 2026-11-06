using ErrorOr;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Loads a committed snapshot before applying the same decision used by locked publication authority.</summary>
public sealed class MediaReconciliationPlanning : IMediaReconciliationPlanning
{
    private readonly IMediaReconciliationSnapshotReader _snapshots;
    private readonly MediaReconciliationDecision _decision;
    public MediaReconciliationPlanning(IMediaReconciliationSnapshotReader snapshots, MediaReconciliationDecision decision)
    {
        this._snapshots = snapshots;
        this._decision = decision;
    }
    public async Task<ErrorOr<MediaReconciliationDirective>> NextAsync(Guid jobId, MediaReconciliationDemandState demand, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        var loaded = await _snapshots.ReadAsync(demand.Subject, ct);
        return loaded.IsError ? loaded.Errors : _decision.Decide(jobId, demand, loaded.Value, ct);
    }
}
