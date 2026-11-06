using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Exact encoding-request custody and its retained independent boot inspection.</summary>
public sealed record RomCdCreationObservation
{
    public RomCdCreationObservation(Guid bootInspectionId, Sha256 sourceRequestSha256, long sourceRequestBytes)
    {
        if (bootInspectionId == Guid.Empty || sourceRequestSha256.IsEmpty || sourceRequestBytes is < 1 or > 524288)
            throw new ArgumentException("CD creation requires exact bounded source-request custody and a retained boot witness.");
        BootInspectionId = bootInspectionId;
        SourceRequestSha256 = sourceRequestSha256;
        SourceRequestBytes = sourceRequestBytes;
    }
    public Guid BootInspectionId { get; }
    public Sha256 SourceRequestSha256 { get; }
    public long SourceRequestBytes { get; }
}
