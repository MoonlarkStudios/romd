using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Measured raw track-local projection; names and original file grouping are not inferred.</summary>
public sealed record RomCdInspectedTrack(RomCdTrack Placement, int AlignmentFrames, int PregapFrames,
    bool PregapStored, int PostgapFrames, RomLogicalIdentity Identity);

/// <summary>Complete stopped-child observation before catalog binding. Carries no original descriptor or encoding provenance.</summary>
public sealed class RomCdInspection
{
    public RomCdInspection(Guid id, RomLogicalIdentity artifactIdentity, RomContainerFacts.Chd container,
        RomChdDecoderBuild decoder, string checkedProfile, string projectionProfile, string verifierBuild,
        DateTimeOffset observedAt, Sha1 computedRawSha1, Sha1 computedOverallSha1,
        IReadOnlyList<RomCdInspectedTrack> tracks, RomPs1CdBoot? boot)
    {
        ArgumentNullException.ThrowIfNull(artifactIdentity);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(tracks);
        var captured = tracks.ToArray();
        if (id == Guid.Empty || container.Version != 5 || container.HasParent || container.UnitBytes != 2448 ||
            container.LogicalBytes % 2448 != 0 || container.LogicalBytes / 2448 is < 1 or > 450000 ||
            computedRawSha1 != container.StoredRawSha1 || computedOverallSha1 != container.StoredOverallSha1 ||
            !Label(checkedProfile) || !Label(projectionProfile) || !Label(verifierBuild) ||
            captured.Length is < 1 or > 99 || container.Metadata.Count != captured.Length)
            throw new ArgumentException("CD inspection requires complete independent container integrity and bounded raw projections.");
        long next = 0;
        for (int index = 0; index < captured.Length; index++)
        {
            var track = captured[index];
            if (track is null || track.Placement is not { } placement || track.Identity is null || placement.Number != index + 1 ||
                placement.FirstFrame != next || placement.Frames is < 1 or > 450000 || !Enum.IsDefined(placement.Kind) ||
                track.AlignmentFrames != (4 - placement.Frames % 4) % 4 || track.PregapFrames is < 0 or >= 450000 ||
                track.PostgapFrames is < 0 or >= 450000 || track.PregapStored && (track.PregapFrames == 0 || track.PregapFrames >= placement.Frames) ||
                track.Identity.Size != placement.Frames * 2352L)
                throw new ArgumentException("Every raw track requires consecutive placement, measured alignment, gap facts and its complete identity.");
            next = checked(next + placement.Frames + track.AlignmentFrames);
        }
        if (next != container.LogicalBytes / 2448 || boot is not null &&
            (captured[0].Placement.Kind == RomCdTrackKind.Audio || boot.FirstTrackIndex01 != (captured[0].PregapStored ? captured[0].PregapFrames : 0) ||
             boot.VolumeBlocks > captured[0].Placement.Frames - boot.FirstTrackIndex01))
            throw new ArgumentException("Track placement and any positive boot observation must fit the observed container.");
        Id = id;
        ArtifactIdentity = artifactIdentity;
        Container = container;
        Decoder = decoder;
        CheckedProfile = checkedProfile;
        ProjectionProfile = projectionProfile;
        VerifierBuild = verifierBuild;
        ObservedAt = observedAt;
        ComputedRawSha1 = computedRawSha1;
        ComputedOverallSha1 = computedOverallSha1;
        Tracks = Array.AsReadOnly(captured);
        Boot = boot;
    }
    public Guid Id { get; }
    public RomLogicalIdentity ArtifactIdentity { get; }
    public RomContainerFacts.Chd Container { get; }
    public RomChdDecoderBuild Decoder { get; }
    public string CheckedProfile { get; }
    public string ProjectionProfile { get; }
    public string VerifierBuild { get; }
    public DateTimeOffset ObservedAt { get; }
    public Sha1 ComputedRawSha1 { get; }
    public Sha1 ComputedOverallSha1 { get; }
    public IReadOnlyList<RomCdInspectedTrack> Tracks { get; }
    public RomPs1CdBoot? Boot { get; }
    private static bool Label(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}
