using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomReconstructionManifestTests
{
    [Theory]
    [InlineData(typeof(RomReconstructionManifest))]
    [InlineData(typeof(RomOutputSelector))]
    public void Variants_BaseConstructors_RejectExternalDerivation(Type baseType)
    {
        // Records synthesize protected copy constructors even with a private default constructor.
        var constructors = baseType.GetConstructors(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        constructors.ShouldNotBeEmpty();
        constructors.ShouldAllBe(constructor => constructor.IsPrivate);
    }

    [Fact]
    public void Member_SameOrdinal_HasStableValueEquality()
    {
        var first = new RomOutputSelector.Member(0);
        var same = new RomOutputSelector.Member(0);
        var other = new RomOutputSelector.Member(1);
        first.Equals(same).ShouldBeTrue();
        first.GetHashCode().ShouldBe(same.GetHashCode());
        first.Equals(other).ShouldBeFalse();
        first.Equals(RomOutputSelector.Whole.Instance).ShouldBeFalse();
    }

    [Fact]
    public void ContainsOutput_WholeFile_AcceptsOnlyWholeSelector()
    {
        var manifest = RomReconstructionManifest.WholeFile.Instance;
        manifest.ContainsOutput(RomOutputSelector.Whole.Instance).ShouldBeTrue();
        manifest.ContainsOutput(new RomOutputSelector.Member(0)).ShouldBeFalse();
    }

    [Fact]
    public void ContainsOutput_CdTracks_AcceptsOnlyItsDecodedBinMembers()
    {
        var manifest = Layout();
        manifest.ContainsOutput(new RomOutputSelector.Member(0)).ShouldBeTrue();
        manifest.ContainsOutput(new RomOutputSelector.Member(1)).ShouldBeFalse(); // Retained CUE, not CHD output.
        manifest.ContainsOutput(new RomOutputSelector.Member(2)).ShouldBeFalse();
        manifest.ContainsOutput(RomOutputSelector.Whole.Instance).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(199)]
    public void Member_InvalidOrdinal_Rejects(int ordinal) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RomOutputSelector.Member(ordinal));

    [Theory]
    [InlineData("unaligned")]
    [InlineData("gap")]
    [InlineData("frame-count")]
    [InlineData("missing-track")]
    [InlineData("duplicate-track")]
    [InlineData("member-size")]
    [InlineData("path-collision")]
    [InlineData("missing-cue")]
    [InlineData("duplicate-cue")]
    [InlineData("undefined-track")]
    public void CdTracks_ContradictoryGeometryOrOwnership_Rejects(string mutation)
    {
        RomCdTrack[] tracks = [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)];
        RomCdFileMember[] members = [Bin("disc.bin", 30, [1, 2]), Cue()];
        long frames = 36;
        switch (mutation)
        {
            case "unaligned":
                tracks[1] = tracks[1] with { FirstFrame = 17 };
                break;
            case "gap":
                tracks[1] = tracks[1] with { FirstFrame = 24 };
                break;
            case "frame-count":
                frames = 35;
                break;
            case "missing-track":
                members[0] = Bin("disc.bin", 17, [1]);
                break;
            case "duplicate-track":
                members = [Bin("one.bin", 17, [1]), Bin("two.bin", 17, [1]), Cue()];
                break;
            case "member-size":
                members[0] = Bin("disc.bin", 29, [1, 2]);
                break;
            case "path-collision":
                members = [Bin("disc.bin", 17, [1]), Bin("DISC.BIN", 13, [2]), Cue()];
                break;
            case "missing-cue":
                members[1] = new RomCdStoredMember("disc.sub", Identity(100), RomCdStoredRole.Sidecar);
                break;
            case "duplicate-cue":
                members = [members[0], Cue(), new RomCdStoredMember("other.cue", Identity(100), RomCdStoredRole.Cue)];
                break;
            case "undefined-track":
                tracks[0] = tracks[0] with { Kind = (RomCdTrackKind)99 };
                break;
        }
        Should.Throw<ArgumentException>(() => new RomReconstructionManifest.CdTracks(frames, tracks, members));
    }

    [Fact]
    public void CdTracks_InputMutation_PreservesValidatedFacts()
    {
        RomCdTrack[] tracks = [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)];
        int[] memberTracks = [1, 2];
        RomCdFileMember[] members = [Bin("disc.bin", 30, memberTracks), Cue()];
        var manifest = new RomReconstructionManifest.CdTracks(36, tracks, members);

        tracks[0] = tracks[0] with { Frames = 1 };
        members[0] = Cue();
        memberTracks[0] = 99;

        manifest.Tracks[0].Frames.ShouldBe(17);
        manifest.Members[0].ShouldBeOfType<RomCdBinMember>().Tracks.ShouldBe([1, 2]);
        Should.Throw<NotSupportedException>(() => ((IList<RomCdTrack>)manifest.Tracks)[0] = tracks[0]);
    }

    private static RomReconstructionManifest.CdTracks Layout() => new(36,
        [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)],
        [Bin("disc.bin", 30, [1, 2]), Cue()]);
    private static RomCdBinMember Bin(string path, int frames, IReadOnlyList<int> tracks) =>
        new(path, Identity(frames * 2352), Identity(frames * 2352), tracks);
    private static RomCdStoredMember Cue() => new("disc.cue", Identity(100), RomCdStoredRole.Cue);
    private static RomCdFileIdentity Identity(long size) => new(size, Sha256.Parse(new string('a', 64)),
        Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(0));
}
