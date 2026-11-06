using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Publishes physical logical observations against the exact retained measured output in the caller's transaction.</summary>
public interface IRomMediaObservationStore
{
    Task<ErrorOr<Success>> StageAsync(int romId, RomRetainedMeasurement retained, RomLogicalInspection observation, CancellationToken ct = default);
}

/// <summary>Publishes encoder/source provenance against a complete retained output measurement before source release.</summary>
public interface IRomMediaCreationStore
{
    Task<ErrorOr<Success>> StageAsync(int romId, RomRetainedMeasurement retained, RomSourceEquivalence comparison,
        RomChdEncodingEvidence? nativeEncoding, CancellationToken ct = default);
}
