using System.Text;
using System.Text.Json.Nodes;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Inspection;

public sealed class MediaWorkerProtocolCdJobTests
{
    private static MediaWorkerReply EncodingReply()
    {
        var hashes = new ChdCdFileHashes(512, new string('a', 64), new string('b', 40), new string('c', 32), "00000000");
        var header = new ChdHeaderFacts(512, 39168, 39168, 2448, 1, ChdCdEncodingRecipe.Codec, ChdCdEncodingRecipe.Codec1, 0, 0);
        return new(MediaWorkerStatus.Encoded, new(42, 42, 1 << 20, "decoder", "worker", null, 1 << 20, 0),
            CdEncoding: new(new string('d', 64), 1024, hashes, header, ChdCdEncodingRecipe.Id, "unix-nice", 10),
            ToolIdentity: new("linux-arm64", ChdmanPin.Tag, ChdmanPin.SourceSha256, 1, 512, new string('e', 64)),
            ToolExecution: new(43, 0, 10, 0, 1000));
    }
    [Fact]
    public void RoundTrip_CdEncoding_UsesDistinctClosedEnvelope()
    {
        byte[] json = MediaWorkerProtocol.Write(EncodingReply());
        Encoding.UTF8.GetString(json).ShouldContain(MediaWorkerProtocol.CdJobVersion);
        MediaWorkerProtocol.TryRead(json, out var reply).ShouldBeTrue();
        reply!.CdEncoding.ShouldBe(EncodingReply().CdEncoding);
        reply.ChdEncoding.ShouldBeNull();
        reply.Inspection.ShouldBeNull();
        reply.CdVerification.ShouldBeNull();
    }
    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("nested-extra")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-codec")]
    [InlineData("null-hashes")]
    [InlineData("bad-digest")]
    [InlineData("tool-pid")]
    [InlineData("tool-exit")]
    [InlineData("tool-output")]
    [InlineData("mixed-worker")]
    [InlineData("mixed-status")]
    public void Read_ContradictoryEncoding_Rejects(string mutation)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(EncodingReply()))!.AsObject();
        switch (mutation)
        {
            case "extra":
                json["unknown"] = 1;
                break;
            case "missing":
                json.Remove("verification");
                break;
            case "nested-extra":
                json["encoding"]!["instructions"] = "run arbitrary decoder";
                break;
            case "wrong-profile":
                json["encoding"]!["recipe"] = "dvd";
                break;
            case "wrong-codec":
                json["encoding"]!["header"]!["codec0"] = 0;
                break;
            case "null-hashes":
                json["encoding"]!["artifact"] = null;
                break;
            case "bad-digest":
                json["encoding"]!["artifact"]!["sha256"] = "abc";
                break;
            case "tool-pid":
                json["execution"]!["processId"] = 42;
                break;
            case "tool-exit":
                json["execution"]!["exitCode"] = 1;
                break;
            case "tool-output":
                json["execution"]!["standardOutputBytes"] = 262145;
                break;
            case "mixed-worker":
                json["worker"]!["nativeIdentity"] = new JsonObject();
                break;
            case "mixed-status":
                json["status"] = "inspected";
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse();
    }
    [Fact]
    public void Read_DuplicateFieldsAndBudgetExpansion_Rejects()
    {
        string json = Encoding.UTF8.GetString(MediaWorkerProtocol.Write(EncodingReply()));
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.Insert(1, "\"status\":\"encoded\",")), out _).ShouldBeFalse();
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json + new string(' ', MediaWorkerProtocol.MaximumCdJobOutputBytes)), out _).ShouldBeFalse();
        byte[] legacy = MediaWorkerProtocol.Write(new(MediaWorkerStatus.Ready, EncodingReply().Identity));
        byte[] oversizedLegacy = [.. legacy, .. Enumerable.Repeat((byte)' ', MediaWorkerProtocol.MaximumOutputBytes)];
        MediaWorkerProtocol.TryRead(oversizedLegacy, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Read_WholeFileVerification_RequiresCompleteMatchingMemberProofAndContainerEvidence()
    {
        var native = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        var measured = await ChdCdInspectionService.InspectAsync(NativeTestBuilds.LibchdrFixture("cd-edge-single.chd"), native, 2L << 30, 2L << 30);
        var hashes = new ChdCdFileHashes(measured.Inspection!.Tracks.Sum(t => t.BinBytes), new string('a', 64), new string('b', 40), new string('c', 32), "12345678");
        var cueHashes = hashes with { Bytes = 98 };
        var reply = new MediaWorkerReply(MediaWorkerStatus.Inspected, EncodingReply().Identity, NativeIdentity: measured.NativeIdentity,
            CdInspection: measured.Inspection, CdVerification: new(ChdCdVerification.CurrentProfile, new string('d', 64),
                [new("disc.bin", "bin", [1, 2, 3], hashes, hashes), new("disc.cue", "cue", [], cueHashes, null)], null));
        byte[] bytes = MediaWorkerProtocol.Write(reply);
        MediaWorkerProtocol.TryRead(bytes, out var parsed).ShouldBeTrue();
        parsed!.CdVerification!.Members.Count.ShouldBe(2);
        var retainedReply = reply with { CdVerification = reply.CdVerification! with { Profile = ChdCdRetainedRequest.CurrentProfile, Ps1Boot = new(Ps1CdIdentifier.Profile, 0, 64, 24, 2048, "TEST.EXE;1", 26, 4096, 0x80010000, 0x80010000, 2048) } };
        // A boot observation for 64 frames cannot describe this tiny non-PS1 measured fixture.
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(retainedReply), out _).ShouldBeFalse();
        foreach (string mutation in new[] { "source-digest", "missing-track", "repeated-track", "foreign-track", "empty-tracks", "cue-projection", "null-members", "role", "profile", "missing-native", "geometry", "field" })
        {
            var json = JsonNode.Parse(bytes)!;
            var members = json["verification"]!["members"]!;
            switch (mutation)
            {
                case "source-digest":
                    members[0]!["original"]!["sha1"] = new string('f', 40);
                    break;
                case "missing-track":
                    members[0]!["tracks"] = new JsonArray(1, 2);
                    break;
                case "repeated-track":
                    members[0]!["tracks"] = new JsonArray(1, 1, 3);
                    break;
                case "foreign-track":
                    members[0]!["tracks"] = new JsonArray(1, 2, 99);
                    break;
                case "empty-tracks":
                    members[0]!["tracks"] = new JsonArray();
                    break;
                case "cue-projection":
                    members[1]!["reconstructed"] = members[1]!["original"]!.DeepClone();
                    break;
                case "null-members":
                    json["verification"]!["members"] = null;
                    break;
                case "role":
                    members[1]!["role"] = "script";
                    break;
                case "profile":
                    json["verification"]!["profile"] = "track-hashes/v1";
                    break;
                case "missing-native":
                    json["native"] = null;
                    break;
                case "geometry":
                    json["inspection"]!["header"]!["unitBytes"] = 2048;
                    break;
                case "field":
                    members[0]!["original"]!["extra"] = 1;
                    break;
            }
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _).ShouldBeFalse(mutation);
        }
    }
}
