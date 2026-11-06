using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomCdManifestTests
{
    [Theory]
    [InlineData("../disc.bin")]
    [InlineData("/disc.bin")]
    [InlineData("C:/disc.bin")]
    [InlineData("a\\disc.bin")]
    [InlineData("a//disc.bin")]
    [InlineData("disc.bin\n")]
    public void Member_UnsafePath_Rejects(string path) =>
        Should.Throw<ArgumentException>(() => new RomCdBinMember(path, Identity(30 * 2352), Identity(30 * 2352), [1, 2]));

    [Fact]
    public void Bin_SourceOutputMismatch_Rejects() => Should.Throw<ArgumentException>(() =>
        new RomCdBinMember("disc.bin", Identity(2352), Identity(4704), [1]));

    [Fact]
    public void Manifest_DuplicateOrMissingTrack_Rejects()
    {
        var exact = Identity(17 * 2352);
        Should.Throw<ArgumentException>(() => Manifest([new RomCdBinMember("disc.bin", exact, exact, [1]), Cue()]));
        Should.Throw<ArgumentException>(() => Manifest([new RomCdBinMember("disc.bin", exact, exact, [1]),
            new RomCdBinMember("other.bin", exact, exact, [1]), Cue()]));
    }

    [Fact]
    public void Manifest_CopiesLists_PreventsPostValidationMutation()
    {
        int[] tracks = [1, 2];
        var member = new RomCdBinMember("disc.bin", Identity(30 * 2352), Identity(30 * 2352), tracks);
        RomCdFileMember[] members = [member, Cue()];
        var manifest = Manifest(members);
        tracks[0] = 99;
        members[0] = Cue();
        ((RomCdBinMember)manifest.Members[0]).Tracks.ShouldBe([1, 2]);
    }

    private static RomCdFileIdentity Identity(long bytes) => new(bytes, Sha256.Parse(new string('a', 64)),
        Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(123));
    private static RomCdStoredMember Cue() => new("disc.cue", Identity(100), RomCdStoredRole.Cue);
    internal static RomReconstructionManifest.CdTracks Manifest(IReadOnlyList<RomCdFileMember>? members = null) => new(
        36,
        [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)],
        members ?? [new RomCdBinMember("disc.bin", Identity(30 * 2352), Identity(30 * 2352), [1, 2]), Cue()]);
}
