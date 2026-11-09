namespace Romd.Domain.Source.Rom;

/// <summary>One retained managed inspection of independently verified CD logical bytes; it implies no encoder provenance.</summary>
public sealed class RomCdBootInspection
{
    public RomCdBootInspection(Guid id, Guid verificationId, string inspectorBuild, DateTimeOffset observedAt, RomCdBoot boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        RomFactLabels.Validate(inspectorBuild, 256);
        if (id == Guid.Empty || verificationId == Guid.Empty)
            throw new ArgumentException("A CD boot inspection requires exact immutable identities.");
        Id = id;
        VerificationId = verificationId;
        InspectorBuild = inspectorBuild;
        ObservedAt = observedAt.ToUniversalTime();
        Boot = boot;
    }
    public Guid Id { get; }
    public Guid VerificationId { get; }
    public string InspectorBuild { get; }
    public DateTimeOffset ObservedAt { get; }
    public RomCdBoot Boot { get; }
    public static bool Fits(RomReconstructionManifest.CdTracks cd, RomCdBoot boot) => Fits(cd.Tracks[0], cd.Tracks.Sum(track => (long)track.Frames), boot);
    public static bool Fits(RomCdTrack first, long totalFrames, RomCdBoot boot) => boot switch
    {
        RomPs1CdBoot ps1 => first.Kind != RomCdTrackKind.Audio && ps1.FirstTrackIndex01 + ps1.VolumeBlocks <= totalFrames &&
            ps1.FirstTrackIndex01 + ps1.RootBlock + (ps1.RootBytes + 2047L) / 2048 <= first.Frames &&
            ps1.FirstTrackIndex01 + ps1.BootBlock + (ps1.BootBytes + 2047L) / 2048 <= first.Frames,
        RomSaturnCdBoot saturn => first.Kind == RomCdTrackKind.Mode1Raw &&
            saturn.FirstTrackIndex01 + (saturn.InitialProgramBytes + 2047L) / 2048 <= first.Frames,
        _ => false
    };
}
