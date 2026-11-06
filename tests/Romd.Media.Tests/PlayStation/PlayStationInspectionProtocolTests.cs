using System.Text;
using System.Text.Json.Nodes;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class PlayStationInspectionProtocolTests
{
    [Theory]
    [InlineData(false, Ps2DvdIdentificationStatus.Identified)]
    [InlineData(true, Ps2DvdIdentificationStatus.Identified)]
    [InlineData(false, Ps2DvdIdentificationStatus.NotIdentified)]
    [InlineData(true, Ps2DvdIdentificationStatus.NotIdentified)]
    [InlineData(false, Ps2DvdIdentificationStatus.Malformed)]
    [InlineData(true, Ps2DvdIdentificationStatus.Malformed)]
    [InlineData(false, Ps2DvdIdentificationStatus.LimitExceeded)]
    [InlineData(true, Ps2DvdIdentificationStatus.LimitExceeded)]
    public void WriteRead_OriginalAndDecodedBootObservation_PreservesDistinctFacts(bool chd, Ps2DvdIdentificationStatus status)
    {
        var reply = Valid(chd) with
        {
            Ps2Inspection = new(status, Ps2DvdIdentifier.ProfileVersion,
            status == Ps2DvdIdentificationStatus.Identified ? Boot : null)
        };

        var wire = MediaWorkerProtocol.Write(reply);

        wire.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumOutputBytes);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
        parsed!.Inspection!.DiscKind.ShouldBeNull("boot layout does not establish DVD media or Nintendo kind");
    }

    [Theory]
    [InlineData("logicalBytes")]
    [InlineData("volumeBlocks")]
    [InlineData("primaryDescriptorBlock")]
    [InlineData("rootDirectoryBlock")]
    [InlineData("rootDirectoryBytes")]
    [InlineData("systemCnfBlock")]
    [InlineData("systemCnfBytes")]
    [InlineData("bootExecutableBlock")]
    [InlineData("bootExecutableBytes")]
    [InlineData("observedBoot2Value")]
    [InlineData("bootPath")]
    [InlineData("bootSerial")]
    [InlineData("entryPoint")]
    [InlineData("programHeaderCount")]
    public void TryRead_MissingBootFact_Rejects(string field)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid(false)))!;
        json["ps2Inspection"]!["facts"]!.AsObject().Remove(field);

        Read(json).ShouldBeFalse();
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("status")]
    [InlineData("identified-without-facts")]
    [InlineData("negative-with-facts")]
    [InlineData("different-logical-length")]
    [InlineData("fractional-block")]
    [InlineData("oversized-volume")]
    [InlineData("descriptor-before-prefix")]
    [InlineData("descriptor-outside-volume")]
    [InlineData("root-in-descriptor")]
    [InlineData("root-in-terminator")]
    [InlineData("cnf-in-terminator")]
    [InlineData("boot-in-terminator")]
    [InlineData("boot-too-short")]
    [InlineData("program-table-outside-boot")]
    [InlineData("root-size-unaligned")]
    [InlineData("root-size-unbounded")]
    [InlineData("cnf-size-unbounded")]
    [InlineData("boot-outside-volume")]
    [InlineData("overlapping-root-cnf")]
    [InlineData("overlapping-cnf-boot")]
    [InlineData("zero-entry")]
    [InlineData("unaligned-entry")]
    [InlineData("too-many-program-headers")]
    [InlineData("zero-program-headers")]
    [InlineData("unsafe-path")]
    [InlineData("foreign-serial")]
    [InlineData("foreign-observed-path")]
    [InlineData("extra-fact")]
    [InlineData("nintendo-kind")]
    [InlineData("rvz-observation")]
    [InlineData("ready-observation")]
    public void TryRead_ContradictoryOrUnboundedBootFacts_Rejects(string control)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid(false)))!;
        var ps2 = json["ps2Inspection"]!;
        var facts = ps2["facts"]!;
        switch (control)
        {
            case "profile":
                ps2["profile"] = "ps2-invented/v1";
                break;
            case "status":
                ps2["status"] = "eligible-dvd";
                break;
            case "identified-without-facts":
                ps2["facts"] = null;
                break;
            case "negative-with-facts":
                ps2["status"] = "not-identified";
                break;
            case "different-logical-length":
                facts["logicalBytes"] = 129024;
                break;
            case "fractional-block":
                facts["rootDirectoryBlock"] = 24.5;
                break;
            case "oversized-volume":
                facts["volumeBlocks"] = 65;
                break;
            case "descriptor-before-prefix":
                facts["primaryDescriptorBlock"] = 15;
                break;
            case "descriptor-outside-volume":
                facts["primaryDescriptorBlock"] = 63;
                break;
            case "root-in-descriptor":
                facts["rootDirectoryBlock"] = 16;
                break;
            case "root-in-terminator":
                facts["rootDirectoryBlock"] = 17;
                break;
            case "cnf-in-terminator":
                facts["systemCnfBlock"] = 17;
                break;
            case "boot-in-terminator":
                facts["bootExecutableBlock"] = 17;
                break;
            case "boot-too-short":
                facts["bootExecutableBytes"] = 83;
                break;
            case "program-table-outside-boot":
                facts["programHeaderCount"] = 15;
                break;
            case "root-size-unaligned":
                facts["rootDirectoryBytes"] = 2049;
                break;
            case "root-size-unbounded":
                facts["rootDirectoryBytes"] = 264192;
                break;
            case "cnf-size-unbounded":
                facts["systemCnfBytes"] = 16385;
                break;
            case "boot-outside-volume":
                facts["bootExecutableBlock"] = 64;
                break;
            case "overlapping-root-cnf":
                facts["systemCnfBlock"] = 24;
                break;
            case "overlapping-cnf-boot":
                facts["bootExecutableBlock"] = 25;
                break;
            case "zero-entry":
                facts["entryPoint"] = 0;
                break;
            case "unaligned-entry":
                facts["entryPoint"] = 1048577;
                break;
            case "too-many-program-headers":
                facts["programHeaderCount"] = 129;
                break;
            case "zero-program-headers":
                facts["programHeaderCount"] = 0;
                break;
            case "unsafe-path":
                facts["bootPath"] = "cdrom0:\\..\\SLUS_203.12;1";
                break;
            case "foreign-serial":
                facts["bootSerial"] = "SLUS_999.99";
                break;
            case "foreign-observed-path":
                facts["observedBoot2Value"] = "cdrom0:\\SLUS_999.99;1";
                break;
            case "extra-fact":
                facts["dvd"] = true;
                break;
            case "nintendo-kind":
                json["inspection"]!["discKind"] = "gamecube";
                json["isoInspection"]!["status"] = "eligible";
                json["isoInspection"]!["layout"] = new JsonObject
                {
                    ["kind"] = "gamecube",
                    ["logicalBytes"] = 131072,
                    ["wiiPartitionCount"] = 0,
                    ["standardKeyPartitions"] = 0,
                    ["koreanKeyPartitions"] = 0
                };
                break;
            case "rvz-observation":
                json["isoInspection"] = null;
                json["inspection"]!["encodingProfile"] = "rvz-import/v1;codec=5;level=19;block=131072;format=01000000;encoder=unknown";
                break;
            case "ready-observation":
                json["status"] = "ready";
                break;
        }

        Read(json).ShouldBeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("profile")]
    [InlineData("count")]
    [InlineData("tag")]
    [InlineData("flags")]
    [InlineData("length")]
    [InlineData("payload")]
    [InlineData("digest")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public void TryRead_AbsentOrAlteredDvdMetadata_RejectsCompleteMeasurement(string control)
    {
        var json = JsonNode.Parse(MediaWorkerProtocol.Write(Valid(true)))!;
        var chd = json["chdInspection"]!;
        var metadata = chd["dvdMetadata"]!;
        switch (control)
        {
            case "missing":
                chd.AsObject().Remove("dvdMetadata");
                break;
            case "null":
                chd["dvdMetadata"] = null;
                break;
            case "profile":
                metadata["profile"] = "chd-dvd-metadata/v99";
                break;
            case "count":
                metadata["entryCount"] = 2;
                break;
            case "tag":
                metadata["tag"] = 0x43485432;
                break;
            case "flags":
                metadata["flags"] = 3;
                break;
            case "length":
                metadata["payloadBytes"] = 2;
                break;
            case "payload":
                metadata["payloadByte"] = 1;
                break;
            case "digest":
                metadata["normalizedSha256"] = new string('a', 64);
                break;
            case "extra":
                metadata["unknown"] = 0;
                break;
        }
        byte[] wire = Encoding.UTF8.GetBytes(json.ToJsonString());
        if (control == "duplicate")
            wire = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(wire)
            .Replace("\"entryCount\":1", "\"entryCount\":1,\"entryCount\":1", StringComparison.Ordinal));

        MediaWorkerProtocol.TryRead(wire, out _).ShouldBeFalse();
    }

    private static Ps2DvdObservedFacts Boot => new(131072, 64, 16, 24, 2048, 25, 32, 26, 512,
        "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x100000, 1);

    private static MediaWorkerReply Valid(bool chd)
    {
        var identity = new MediaWorkerIdentity(41, 41, 512L << 20, "decoder/exact", "worker/exact", null, 512L << 20, 0);
        var measurement = new MediaInspection(new string('a', 64), chd ? 16384 : 131072,
            new string(chd ? 'b' : 'a', 64), new string('c', 40), new string('d', 32), "00000000", 131072, new string('0', 64),
            chd ? "chd-import/v1" : "identity/v1");
        var boot = new MediaPs2Inspection(Ps2DvdIdentificationStatus.Identified, Ps2DvdIdentifier.ProfileVersion, Boot);
        if (!chd)
            return new(MediaWorkerStatus.Inspected, identity, measurement,
            new(NintendoIsoInspectionStatus.Unsupported, NintendoIsoInspector.ProfileVersion), Ps2Inspection: boot);
        var native = new ChdNativeIdentity("linux-arm64", "libmoonlark_chdr.so", 1234, new string('a', 64),
            new string('d', 40), "fixture-upstream", 1, "fixture/v1", new string('e', 64), true, true, true);
        return new(MediaWorkerStatus.Inspected, identity, measurement, NativeIdentity: native,
            ChdInspection: new(5, new(16384, 131072, 16384, 2048, 8, 0x7a6c6962, 0, 0, 0),
                new string('c', 40), new string('c', 40), new string('f', 40), new string('f', 40),
                new("chd-dvd-metadata/v1", 1, 0x44564420, 1, 1, 0,
                    "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406")), Ps2Inspection: boot);
    }

    private static bool Read(JsonNode json) => MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _);
}
