using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Independently observes retained original bytes. Encoded inputs use the retained-media verifier instead.</summary>
public interface IRomOriginalMediaInspection
{
    Task<ErrorOr<IRomOriginalMediaInspectionLease>> AcquireAsync(Guid jobId, MediaReconciliationInput.Logical source,
        RomDecoderIdentity.Managed decoder, CancellationToken ct = default);
}
public interface IRomOriginalMediaInspectionLease : IAsyncDisposable
{
    bool ProcessTreeStopped { get; }
    Task<ErrorOr<RomMediaVerificationMeasurement.Whole>> InspectAsync(CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default);
}
