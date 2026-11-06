namespace Romd.Domain.Source.Rom;

/// <summary>One retained managed inspection of independently verified CD logical bytes; it implies no encoder provenance.</summary>
public sealed class RomCdBootInspection
{
    public RomCdBootInspection(Guid id, Guid verificationId, string inspectorBuild, DateTimeOffset observedAt, RomPs1CdBoot boot)
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
    public RomPs1CdBoot Boot { get; }
    public static bool Fits(RomReconstructionManifest.CdTracks cd, RomPs1CdBoot boot) =>
        cd.Tracks[0].Kind != RomCdTrackKind.Audio && boot.FirstTrackIndex01 + boot.VolumeBlocks <= cd.Tracks.Sum(track => (long)track.Frames) &&
        boot.FirstTrackIndex01 + boot.RootBlock + (boot.RootBytes + 2047L) / 2048 <= cd.Tracks[0].Frames &&
        boot.FirstTrackIndex01 + boot.BootBlock + (boot.BootBytes + 2047L) / 2048 <= cd.Tracks[0].Frames;
}
