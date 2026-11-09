using System.Text;
using System.Text.Json.Nodes;
using Romd.Media.Cd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Cd;

public sealed partial class ChdCdInspectionProtocolTests
{
    private static MediaWorkerReply BootReply(bool job)
    {
        var reply = Valid();
        var before = reply.CdInspection!;
        var tracks = new[] { before.Tracks[0] with { Frames = 64, AlignmentFrames = 0, BinBytes = 64 * 2352 }, before.Tracks[1] with { FirstFrame = 64 } };
        var boot = new Ps1CdObservedFacts(Ps1CdIdentifier.Profile, 0, 64, 24, 2048, "TEST.EXE;1", 26, 4096, 0x80010000, 0x80010000, 2048);
        var inspection = before with
        {
            Header = before.Header with { LogicalBytes = 68 * 2448, HunkCount = 9 },
            Tracks = tracks,
            Metadata = tracks.Select(Metadata).ToArray(),
            Ps1Boot = boot
        };
        var hashes = new ChdCdFileHashes(67 * 2352, new string('a', 64), new string('b', 40), new string('c', 32), "00000000");
        return reply with
        {
            CdInspection = inspection,
            CdVerification = job ? new(ChdCdRetainedRequest.CurrentProfile, new string('e', 64),
            [new("disc.bin", "bin", [1, 2], hashes, hashes), new("disc.cue", "cue", [], hashes with { Bytes = 98 }, null)], boot) : null
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRead_NativeBootObservation_IsRetainedInBothClosedCdEnvelopes(bool job)
    {
        var reply = BootReply(job);
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed!.CdInspection!.Ps1Boot.ShouldBe(reply.CdInspection!.Ps1Boot);
        parsed.CdVerification?.Ps1Boot.ShouldBe(reply.CdVerification?.Ps1Boot);
    }

    [Fact]
    public void WriteRead_NativeBootWithoutARequiredOriginalBootComparison_RemainsAnIndependentObservation()
    {
        var reply = BootReply(true);
        reply = reply with { CdVerification = reply.CdVerification! with { Profile = ChdCdVerification.CurrentProfile, Ps1Boot = null } };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed!.CdInspection!.Ps1Boot.ShouldNotBeNull();
        parsed.CdVerification!.Ps1Boot.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRead_RetainedVerification_RequiresAnExplicitBootFieldAndExactNativeAgreement(bool hasBoot)
    {
        var reply = BootReply(true);
        if (!hasBoot)
            reply = reply with { CdInspection = reply.CdInspection! with { Ps1Boot = null }, CdVerification = reply.CdVerification! with { Ps1Boot = null } };
        byte[] wire = MediaWorkerProtocol.Write(reply);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed!.CdVerification!.Ps1Boot.ShouldBe(reply.CdInspection!.Ps1Boot);
        foreach (string mutation in new[] { "missing-field", "contradiction", "previous-profile", "previous-envelope" })
        {
            var root = JsonNode.Parse(wire)!;
            switch (mutation)
            {
                case "missing-field":
                    root["verification"]!.AsObject().Remove("ps1Boot");
                    break;
                case "contradiction":
                    root[hasBoot ? "verification" : "inspection"]!["ps1Boot"] = hasBoot ? null : JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(true)))!["inspection"]!["ps1Boot"]!.DeepClone();
                    break;
                case "previous-profile":
                    root["verification"]!["profile"] = "chd-cd-retained-files/v1";
                    break;
                case "previous-envelope":
                    root["version"] = "romd-media-cd/v3";
                    break;
            }
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse(mutation);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-boot-field")]
    [InlineData("unknown-field")]
    [InlineData("wrong-profile")]
    [InlineData("negative-index")]
    [InlineData("index-disagreement")]
    [InlineData("volume-overrun")]
    [InlineData("root-overrun")]
    [InlineData("boot-overrun")]
    [InlineData("null-profile")]
    [InlineData("wrong-type")]
    public void TryRead_MissingOrContradictoryNativeBoot_RejectsBothCdEnvelopes(string mutation)
    {
        foreach (bool job in new[] { false, true })
        {
            var root = JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(job)))!;
            var cd = root[job ? "inspection" : "cdInspection"]!.AsObject();
            var boot = cd["ps1Boot"]!.AsObject();
            switch (mutation)
            {
                case "missing":
                    cd.Remove("ps1Boot");
                    break;
                case "missing-boot-field":
                    boot.Remove("firstTrackIndex01");
                    break;
                case "unknown-field":
                    boot["datMatch"] = true;
                    break;
                case "wrong-profile":
                    boot["profile"] = "not-native-observation";
                    break;
                case "negative-index":
                    boot["firstTrackIndex01"] = -1;
                    break;
                case "index-disagreement":
                    boot["firstTrackIndex01"] = 1;
                    break;
                case "volume-overrun":
                    boot["volumeBlocks"] = 68;
                    break;
                case "root-overrun":
                    boot["rootBlock"] = 63;
                    boot["rootBytes"] = 4096;
                    break;
                case "boot-overrun":
                    boot["bootBlock"] = 63;
                    break;
                case "null-profile":
                    boot["profile"] = null;
                    break;
                case "wrong-type":
                    cd["ps1Boot"] = true;
                    break;
            }
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse($"{mutation}, job={job}");
        }
    }

    [Fact]
    public void TryRead_OriginalBootComparisonDisagreesWithNativeBoot_Rejects()
    {
        var reply = BootReply(true);
        reply = reply with { CdVerification = reply.CdVerification! with { Ps1Boot = reply.CdInspection!.Ps1Boot! with { EntryPoint = 0x80020000 } } };
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryRead_NativeBootAtItsByteBudget_AdmitsOnlyTheBoundedObject(bool job, bool oversized)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(job)))!;
        string boot = root[job ? "inspection" : "cdInspection"]!["ps1Boot"]!.ToJsonString();
        int padding = 1024 - Encoding.UTF8.GetByteCount(boot) + (oversized ? 1 : 0);
        string wire = root.ToJsonString().Replace(boot, boot.Insert(1, new string(' ', padding)), StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(wire), out _).ShouldBe(!oversized);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryRead_OversizedOrNestedNativeBootValue_RefusesWithoutThrowing(bool job, bool nestedDuplicate)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(job)))!;
        var boot = root[job ? "inspection" : "cdInspection"]!["ps1Boot"]!;
        boot["bootPath"] = new string('x', 1100);
        string wire = root.ToJsonString();
        if (nestedDuplicate)
            wire = wire.Replace("\"" + new string('x', 1100) + "\"", "{\"x\":1,\"x\":2}", StringComparison.Ordinal);
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(wire), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, "root")]
    [InlineData(true, "root")]
    [InlineData(false, "boot")]
    [InlineData(true, "boot")]
    [InlineData(false, "unaligned-entrypoint")]
    [InlineData(true, "unaligned-entrypoint")]
    public void TryRead_BootExtentsBeyondTheVolumeOrUnalignedCode_RejectsEvenWhenTheyFitTheDataTrack(bool job, string mutation)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(job)))!;
        var boot = root[job ? "inspection" : "cdInspection"]!["ps1Boot"]!;
        if (mutation == "unaligned-entrypoint")
            boot["entryPoint"] = 0x80010001U;
        else
        {
            boot["volumeBlocks"] = 27;
            if (mutation == "root")
            { boot["rootBytes"] = 8192; boot["bootBlock"] = 20; }
        }
        if (job)
            root["verification"]!["ps1Boot"] = boot.DeepClone();
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse();
    }

}
