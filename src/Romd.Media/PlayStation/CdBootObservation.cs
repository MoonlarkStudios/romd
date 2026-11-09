using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.PlayStation;

/// <summary>Exactly one independently recognized CD family, or no positive observation.</summary>
public readonly record struct CdBootObservation(Ps1CdObservedFacts? Ps1, SaturnCdObservedFacts? Saturn)
{
    public string? Profile => Ps1?.Profile ?? Saturn?.Profile;
    public bool Valid => !(Ps1 is not null && Saturn is not null) &&
        (Ps1 is null || Ps1CdIdentifier.Valid(Ps1)) && (Saturn is null || SaturnCdIdentifier.Valid(Saturn));

    public bool Fits(ChdCdTrackType type, long firstFrames, long totalFrames) => Valid &&
        (Ps1 is null || type is ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw &&
            Ps1.FirstTrackIndex01 + Ps1.VolumeBlocks <= totalFrames &&
            Ps1.FirstTrackIndex01 + Ps1.RootBlock + (Ps1.RootBytes + 2047L) / 2048 <= firstFrames &&
            Ps1.FirstTrackIndex01 + Ps1.BootBlock + (Ps1.BootBytes + 2047L) / 2048 <= firstFrames) &&
        (Saturn is null || type == ChdCdTrackType.Mode1Raw &&
            Saturn.FirstTrackIndex01 + (Saturn.InitialProgramBytes + 2047L) / 2048 <= firstFrames);

    public static CdBootObservation Inspect(ISynchronousRawContent bin, ChdCdTrackType type, long index01, long totalFrames, CancellationToken ct)
    {
        var observed = new CdBootObservation(Ps1CdIdentifier.Inspect(bin, type, index01, totalFrames, ct),
            SaturnCdIdentifier.Inspect(bin, type, index01, ct));
        return observed.Valid ? observed : throw new InvalidDataException("Contradictory CD-family boot observations.");
    }
    public static bool Supports(string? profile) => profile is null or Ps1CdIdentifier.Profile or SaturnCdIdentifier.Profile;
}
