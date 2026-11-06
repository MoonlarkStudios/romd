using System.Text;
using Moonlark.Libchdr;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomCdMeasurementMapperTests
{
    private static ChdNativeIdentity Native => new("linux-arm64", "libmoonlark_chdr.so", 1234, new string('a', 64), new string('f', 40), "upstream", 2, "fixture/v1", new string('e', 64), true, true, true);
    private static readonly DateTimeOffset MeasuredAt = DateTimeOffset.UnixEpoch.AddTicks(12345);
    private static ChdCdFileHashes Identity(long bytes, char value) => new(bytes, new string(value, 64), new string(value, 40), new string(value, 32), "00000000");
    private static ChdCdInspection Inspection()
    {
        ChdCdTrackInspection[] tracks = [new(1,ChdCdTrackType.Mode2Raw,0,3,1,0,false,0,7056,new string('a',64),new string('a',40),new string('a',32),"00000000"),
            new(2,ChdCdTrackType.Audio,4,3,1,1,true,0,7056,new string('b',64),new string('b',40),new string('b',32),"00000000")];
        return new(ChdCdInspectionService.Profile, ChdCdInspectionService.ProjectionProfile, new string('d', 64), 5, false,
            new(2000, 8 * 2448, 4 * 2448, 2448, 2, 0, 0, 0, 0), new string('e', 40), new string('e', 40), new string('f', 40), new string('f', 40), tracks,
            [new(0x43485432,1,Convert.ToHexStringLower(Encoding.ASCII.GetBytes("TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"))),
             new(0x43485432,1,Convert.ToHexStringLower(Encoding.ASCII.GetBytes("TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:1 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0")))], null);
    }
    private static ChdCdVerification Verification() => new(ChdCdVerification.CurrentProfile, new string('c', 64),
        [new("a.bin","bin",[1],Identity(7056,'a'),Identity(7056,'a')),new("b.bin","bin",[2],Identity(7056,'b'),Identity(7056,'b')),
         new("disc.cue","cue",[],Identity(10,'c'),null)], null);

    [Fact]
    public void Map_CompleteIndependentMembers_KeepsRawContainerLayoutAndExactOutputMeasurements()
    {
        var inspection = Inspection();
        var verification = Verification();
        var measured = RomCdMeasurementMapper.Map(inspection, verification, Native, "actual-verifier", MeasuredAt);
        var container = measured.Container.ShouldBeOfType<RomContainerFacts.Chd>();
        container.Version.ShouldBe(5U);
        container.UnitBytes.ShouldBe(2448U);
        container.Metadata.Count.ShouldBe(2);
        Convert.ToHexStringLower(container.Metadata[1].Payload).ShouldBe(inspection.Metadata[1].PayloadHex);
        var layout = measured.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>();
        layout.Members.Count.ShouldBe(3);
        layout.Tracks[1].FirstFrame.ShouldBe(4);
        layout.Members[2].ShouldBeOfType<RomCdStoredMember>().Role.ShouldBe(RomCdStoredRole.Cue);
        measured.Measurement.Outputs.Select(output => output.Selector).ShouldBe([new RomOutputSelector.Member(0), new RomOutputSelector.Member(1)]);
        measured.Measurement.Outputs[0].Identity.Sha256.ShouldBe(Sha256.Parse(new string('a', 64)));
        measured.Measurement.ComputedRawSha1.ShouldNotBe(measured.Measurement.Outputs[0].Identity.Sha1);
        measured.Measurement.ObservedAt.ShouldBe(MeasuredAt);
        measured.Measurement.VerifierBuild.ShouldBe("actual-verifier");
        measured.Measurement.Decoder.ShouldBe(new RomDecoderIdentity.Native(new("linux-arm64", Sha1.Parse(new string('f', 40)), 2)));
    }

    [Theory]
    [InlineData("missing-bin")]
    [InlineData("missing-cue")]
    [InlineData("wrong-reconstruction")]
    [InlineData("bad-selector")]
    [InlineData("raw-integrity")]
    [InlineData("overall-integrity")]
    [InlineData("missing-metadata")]
    [InlineData("parent")]
    [InlineData("version")]
    public void Map_IncompleteOrContradictoryIndependentMeasurement_Rejects(string mutation)
    {
        var inspection = Inspection();
        var verification = Verification();
        verification = mutation switch
        {
            "missing-bin" => verification with { Members = verification.Members.Skip(1).ToArray() },
            "missing-cue" => verification with { Members = verification.Members.Take(2).ToArray() },
            "wrong-reconstruction" => verification with { Members = [verification.Members[0] with { Reconstructed = Identity(7056, 'b') }, .. verification.Members.Skip(1)] },
            "bad-selector" => verification with { Members = [verification.Members[0] with { Tracks = [2] }, .. verification.Members.Skip(1)] },
            _ => verification
        };
        inspection = mutation switch
        {
            "raw-integrity" => inspection with { ComputedRawSha1 = new string('a', 40) },
            "overall-integrity" => inspection with { ComputedOverallSha1 = new string('a', 40) },
            "missing-metadata" => inspection with { Metadata = [] },
            "parent" => inspection with { HasParent = true },
            "version" => inspection with { Version = 4 },
            _ => inspection
        };
        var error = Record.Exception(() => RomCdMeasurementMapper.Map(inspection, verification, Native, "actual-verifier", MeasuredAt));
        error.ShouldNotBeNull();
        (error is ArgumentException or InvalidDataException).ShouldBeTrue();
    }
}
