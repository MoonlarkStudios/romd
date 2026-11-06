using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdCdRetainedRequestTests
{
    private static Ps1CdObservedFacts Boot => new(Ps1CdIdentifier.Profile, 0, 64, 24, 2048, "TEST.EXE;1", 26, 4096, 0x80010000, 0x80010000, 2048);
    private static ChdCdRetainedRequest Request(Ps1CdObservedFacts? boot)
    {
        var bin = new ChdCdFileHashes(64 * 2352, new string('a', 64), new string('b', 40), new string('c', 32), "00000000");
        var cue = new ChdCdFileHashes(98, new string('d', 64), new string('e', 40), new string('f', 32), "00000000");
        return new(ChdCdRetainedRequest.CurrentProfile, new string('f', 64), 512, 64, 39168, [0, 0, 0, 0], new string('a', 40), new string('b', 40),
            [new(1, 0, 64, ChdCdTrackType.Mode1Raw)], [new("disc.bin", "bin", [1], bin, bin), new("disc.cue", "cue", [], cue, null)],
            [new("disc.cue", Path.GetFullPath("disc.cue"), cue.Bytes, cue.Sha256, "cue")], boot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_BootField_IsRequiredEvenWhenNoPositiveFactExists(bool hasBoot)
    {
        var request = Request(hasBoot ? Boot : null);
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(request, ChdCdJson.Options);
        var read = ChdCdJson.Read<ChdCdRetainedRequest>(wire, ChdCdRequest.MaximumBytes);
        read.Validate();
        read.Boot.ShouldBe(request.Boot);
        var json = JsonNode.Parse(wire)!.AsObject();
        json.Remove("boot");
        Should.Throw<JsonException>(() => ChdCdJson.Read<ChdCdRetainedRequest>(Encoding.UTF8.GetBytes(json.ToJsonString()), ChdCdRequest.MaximumBytes));
        Should.Throw<InvalidDataException>(() => (request with { Profile = "chd-cd-retained-files/v1" }).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesVerification_AllMemberIdentitiesRemainMandatoryWithoutAPriorBoot(bool observedBoot)
    {
        var request = Request(null);
        var proof = new ChdCdVerification(ChdCdRetainedRequest.CurrentProfile, new string('a', 64), request.Members, observedBoot ? Boot : null);
        request.MatchesVerification(proof).ShouldBeTrue();
        var bin = proof.Members[0];
        var wrong = bin.Original with { Sha256 = new string('e', 64) };
        request.MatchesVerification(proof with { Members = [bin with { Original = wrong, Reconstructed = wrong }, proof.Members[1]] }).ShouldBeFalse();
        request.MatchesVerification(proof with { Members = [bin] }).ShouldBeFalse();
    }

    [Fact]
    public void MatchesVerification_PriorPositiveBoot_CannotDisappearOrChange()
    {
        var request = Request(Boot);
        var proof = new ChdCdVerification(ChdCdRetainedRequest.CurrentProfile, new string('a', 64), request.Members, Boot);
        request.MatchesVerification(proof).ShouldBeTrue();
        request.MatchesVerification(proof with { Ps1Boot = null }).ShouldBeFalse();
        request.MatchesVerification(proof with { Ps1Boot = Boot with { EntryPoint = 0x80020000 } }).ShouldBeFalse();
    }
}
