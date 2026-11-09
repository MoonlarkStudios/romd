using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Romd.Storage;

namespace Romd.Media.Cd;

/// <summary>Exactly one independently recognized CD family, or no positive observation.</summary>
public readonly record struct CdBootObservation
{
    private CdBootObservation(Ps1CdObservedFacts? ps1, SaturnCdObservedFacts? saturn)
    {
        Ps1 = ps1;
        Saturn = saturn;
    }
    public static CdBootObservation None => default;
    public Ps1CdObservedFacts? Ps1 { get; }
    public SaturnCdObservedFacts? Saturn { get; }
    public string? Profile => Ps1?.Profile ?? Saturn?.Profile;

    /// <summary>Validates the nullable wire fields before constructing a mutually exclusive internal observation.</summary>
    public static CdBootObservation FromWire(Ps1CdObservedFacts? ps1, SaturnCdObservedFacts? saturn)
    {
        if (ps1 is not null && (saturn is not null || !Ps1CdIdentifier.Valid(ps1)) ||
            saturn is not null && !SaturnCdIdentifier.Valid(saturn))
            throw new InvalidDataException("Invalid or contradictory CD-family boot observations.");
        return new(ps1, saturn);
    }

    public bool Fits(ChdCdTrackType type, long firstFrames, long totalFrames) =>
        (Ps1 is null || type is ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw &&
            Ps1.FirstTrackIndex01 + Ps1.VolumeBlocks <= totalFrames &&
            Ps1.FirstTrackIndex01 + Ps1.RootBlock + (Ps1.RootBytes + 2047L) / 2048 <= firstFrames &&
            Ps1.FirstTrackIndex01 + Ps1.BootBlock + (Ps1.BootBytes + 2047L) / 2048 <= firstFrames) &&
        (Saturn is null || type == ChdCdTrackType.Mode1Raw &&
            Saturn.FirstTrackIndex01 + (Saturn.InitialProgramBytes + 2047L) / 2048 <= firstFrames);

    public static CdBootObservation Inspect(ISynchronousRawContent bin, ChdCdTrackType type, long index01, long totalFrames, CancellationToken ct)
    {
        return FromWire(Ps1CdIdentifier.Inspect(bin, type, index01, totalFrames, ct),
            SaturnCdIdentifier.Inspect(bin, type, index01, ct));
    }
    public static bool Supports(string? profile) => profile is null or Ps1CdIdentifier.Profile or SaturnCdIdentifier.Profile;
}
