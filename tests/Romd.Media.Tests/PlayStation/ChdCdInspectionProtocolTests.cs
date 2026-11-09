using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Libchdr;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed partial class ChdCdInspectionProtocolTests
{
    private static MediaWorkerReply Valid(int trackCount = 2)
    {
        var tracks = Enumerable.Range(1, trackCount).Select(number => new ChdCdTrackInspection(
            (uint)number, number == 1 ? ChdCdTrackType.Mode2Raw : ChdCdTrackType.Audio,
            (ulong)(number - 1) * 4, 3, 1, number == 1 ? 0U : 1U, number > 1, 0,
            3 * 2352, new string('a', 64), new string('b', 40), new string('c', 32), "00000000")).ToArray();
        return new(MediaWorkerStatus.Inspected,
            new(41, 41, 512L << 20, "decoder/mvid:exact", "worker/mvid:exact", null, 512L << 20, 0),
            NativeIdentity: ChdNativeCandidateTests.PrototypeIdentity(),
            CdInspection: new(ChdCdInspectionService.Profile, ChdCdInspectionService.ProjectionProfile,
                new string('a', 64), 5, false, new(200000, (ulong)trackCount * 4 * 2448, 2448 * 8, 2448,
                    (uint)((trackCount + 1) / 2), (uint)ChdCodec.CdLzma, (uint)ChdCodec.CdFlac, 0, 0),
                new string('d', 40), new string('d', 40), new string('e', 40), new string('e', 40), tracks, tracks.Select(Metadata).ToArray(), null));
    }

    private static ChdCdMetadataInspection Metadata(ChdCdTrackInspection track)
    {
        string type = track.Type switch { ChdCdTrackType.Audio => "AUDIO", ChdCdTrackType.Mode1Raw => "MODE1_RAW", _ => "MODE2_RAW" };
        string payload = FormattableString.Invariant($"TRACK:{track.Number} TYPE:{type} SUBTYPE:NONE FRAMES:{track.Frames} PREGAP:{track.PregapFrames} PGTYPE:{(track.PregapStored ? "V" : "")}{type} PGSUB:NONE POSTGAP:{track.PostgapFrames}");
        return new(ChdMetadataTag.CdTrackV2.Value, 1, Convert.ToHexStringLower(Encoding.ASCII.GetBytes(payload)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void WriteRead_CompleteCdMeasurement_BoundedAndSeparateFromDvdEvidence(int trackCount)
    {
        var original = Valid(trackCount);
        byte[] wire = MediaWorkerProtocol.Write(original);

        wire.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumCdOutputBytes);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed!.Inspection.ShouldBeNull();
        parsed.ChdInspection.ShouldBeNull();
        parsed.Ps2Inspection.ShouldBeNull();
        parsed.NativeIdentity.ShouldBe(original.NativeIdentity);
        parsed.CdInspection!.Tracks.ShouldBe(original.CdInspection!.Tracks);
        parsed.CdInspection.Metadata.ShouldBe(original.CdInspection.Metadata);
        (parsed.CdInspection with { Tracks = original.CdInspection.Tracks, Metadata = original.CdInspection.Metadata }).ShouldBe(original.CdInspection);
    }

    [Fact]
    public void WriteRead_MaximumRawMetadata_PreservesOrderAndFitsBound()
    {
        var original = Valid(99);
        var metadata = original.CdInspection!.Metadata.Reverse().Select(entry => entry with
        {
            PayloadHex = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Convert.FromHexString(entry.PayloadHex)).PadRight(511, ' ') + "\0"))
        }).ToArray();
        original = original with { CdInspection = original.CdInspection with { Metadata = metadata } };
        byte[] wire = MediaWorkerProtocol.Write(original);
        wire.Length.ShouldBeLessThan(MediaWorkerProtocol.MaximumCdOutputBytes);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed!.CdInspection!.Metadata.ShouldBe(metadata);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("too-long")]
    [InlineData("invalid-hex")]
    [InlineData("unknown-field")]
    [InlineData("wrong-tag")]
    [InlineData("unchecked")]
    [InlineData("missing-track")]
    [InlineData("duplicate-track")]
    [InlineData("layout-disagreement")]
    [InlineData("does-not-fit-artifact")]
    public void TryRead_IncompleteOrContradictoryRawMetadata_Rejects(string mutation)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(Valid()))!;
        var cd = root["cdInspection"]!.AsObject();
        var metadata = cd["metadata"]!.AsArray();
        switch (mutation)
        {
            case "missing":
                cd.Remove("metadata");
                break;
            case "null":
                cd["metadata"] = null;
                break;
            case "empty":
                metadata[0]!["payload"] = "";
                break;
            case "too-long":
                metadata[0]!["payload"] = new string('a', 1026);
                break;
            case "invalid-hex":
                metadata[0]!["payload"] = "xx";
                break;
            case "unknown-field":
                metadata[0]!["kind"] = "cd";
                break;
            case "wrong-tag":
                metadata[0]!["tag"] = ChdMetadataTag.Dvd.Value;
                break;
            case "unchecked":
                metadata[0]!["flags"] = 0;
                break;
            case "missing-track":
                metadata.RemoveAt(1);
                break;
            case "duplicate-track":
                metadata[1] = metadata[0]!.DeepClone();
                break;
            case "layout-disagreement":
                cd["tracks"]![1]!["postgapFrames"] = 1;
                break;
            case "does-not-fit-artifact":
                cd["header"]!["artifactBytes"] = 124;
                break;
        }
        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void TryRead_ExtendedResponseBudget_OnlyAvailableToCdVariant(bool cd, bool oversized, bool accepted)
    {
        var reply = cd ? Valid() : Valid() with { Status = MediaWorkerStatus.Ready, CdInspection = null, NativeIdentity = null };
        string wire = Encoding.UTF8.GetString(MediaWorkerProtocol.Write(reply));
        int bytes = (oversized ? MediaWorkerProtocol.MaximumCdOutputBytes : MediaWorkerProtocol.MaximumOutputBytes) + 1;
        byte[] padded = Encoding.UTF8.GetBytes(wire + new string(' ', bytes - Encoding.UTF8.GetByteCount(wire)));

        MediaWorkerProtocol.TryRead(padded, out _).ShouldBe(accepted);
    }

    [Theory]
    [InlineData("old-version")]
    [InlineData("missing-payload")]
    [InlineData("missing-native")]
    [InlineData("wrong-status")]
    [InlineData("mixed-inspection")]
    [InlineData("mixed-ps2")]
    [InlineData("mixed-encoding")]
    [InlineData("mixed-rejection")]
    [InlineData("unknown-payload-field")]
    [InlineData("profile")]
    [InlineData("projection-profile")]
    [InlineData("raw-mismatch")]
    [InlineData("overall-mismatch")]
    [InlineData("missing-hash")]
    [InlineData("container-version")]
    [InlineData("parent")]
    [InlineData("artifact-limit")]
    [InlineData("logical-limit")]
    [InlineData("hunk-limit")]
    [InlineData("hunk-geometry")]
    [InlineData("hunk-count")]
    [InlineData("unit")]
    [InlineData("codec")]
    [InlineData("no-tracks")]
    [InlineData("too-many-tracks")]
    [InlineData("ordinal")]
    [InlineData("track-order")]
    [InlineData("track-overlap")]
    [InlineData("track-gap")]
    [InlineData("track-coverage")]
    [InlineData("track-length")]
    [InlineData("track-alignment")]
    [InlineData("track-zero-frames")]
    [InlineData("track-negative-frames")]
    [InlineData("track-type")]
    [InlineData("track-hash")]
    [InlineData("track-pregap")]
    [InlineData("track-gap-limit")]
    [InlineData("track-gap-boolean")]
    [InlineData("track-unknown-field")]
    public void TryRead_ContradictoryOrMalformedCdFacts_Rejects(string mutation)
    {
        var root = JsonNode.Parse(MediaWorkerProtocol.Write(Valid()))!;
        var cd = root["cdInspection"]!;
        var header = cd["header"]!;
        var tracks = cd["tracks"]!.AsArray();
        var track = tracks[0]!;
        switch (mutation)
        {
            case "old-version":
                root["version"] = "romd-media/v7";
                break;
            case "missing-payload":
                root.AsObject().Remove("cdInspection");
                break;
            case "missing-native":
                root["nativeIdentity"] = null;
                break;
            case "wrong-status":
                root["status"] = "ready";
                break;
            case "mixed-inspection":
                root["inspection"] = new JsonObject();
                break;
            case "mixed-ps2":
                root["ps2Inspection"] = new JsonObject();
                break;
            case "mixed-encoding":
                root["chdEncoding"] = new JsonObject();
                break;
            case "mixed-rejection":
                root["chdRejection"] = new JsonObject();
                break;
            case "unknown-payload-field":
                cd["originalCue"] = "invented.cue";
                break;
            case "profile":
                cd["profile"] = "chd-import/v1";
                break;
            case "projection-profile":
                cd["projectionProfile"] = "unknown";
                break;
            case "raw-mismatch":
                cd["computedRawSha1"] = new string('f', 40);
                break;
            case "overall-mismatch":
                cd["computedOverallSha1"] = new string('f', 40);
                break;
            case "missing-hash":
                cd["artifactSha256"] = new string('0', 64);
                break;
            case "parent":
                header["hasParent"] = true;
                break;
            case "container-version":
                header["version"] = 4;
                break;
            case "artifact-limit":
                header["artifactBytes"] = ChdCdLimits.MaximumArtifactBytes + 1;
                break;
            case "logical-limit":
                header["logicalBytes"] = ChdCdLimits.MaximumLogicalBytes + 2448;
                break;
            case "hunk-limit":
                header["hunkBytes"] = ChdCdLimits.MaximumHunkBytes + 2448;
                break;
            case "hunk-geometry":
                header["hunkBytes"] = 2048;
                break;
            case "hunk-count":
                header["hunkCount"] = 2;
                break;
            case "unit":
                header["unitBytes"] = 2048;
                break;
            case "codec":
                header["codec0"] = uint.MaxValue;
                break;
            case "no-tracks":
                tracks.Clear();
                break;
            case "too-many-tracks":
                for (int index = tracks.Count; index < 100; index++)
                    tracks.Add(track.DeepClone());
                break;
            case "ordinal":
                track["number"] = 2;
                break;
            case "track-order":
                tracks[1]!["number"] = 1;
                break;
            case "track-overlap":
                tracks[1]!["firstFrame"] = 0;
                break;
            case "track-gap":
                tracks[1]!["firstFrame"] = 8;
                break;
            case "track-coverage":
                header["logicalBytes"] = 2448 * 7;
                break;
            case "track-length":
                track["binBytes"] = 2352;
                break;
            case "track-alignment":
                track["alignmentFrames"] = 0;
                break;
            case "track-zero-frames":
                track["frames"] = 0;
                break;
            case "track-negative-frames":
                track["frames"] = -1;
                break;
            case "track-type":
                track["type"] = "mode2-cooked";
                break;
            case "track-hash":
                track["sha256"] = new string('A', 64);
                break;
            case "track-pregap":
                track["pregapStored"] = true;
                break;
            case "track-gap-limit":
                track["postgapFrames"] = ChdCdLimits.MaximumFrames + 1;
                break;
            case "track-gap-boolean":
                track["pregapStored"] = "true";
                break;
            case "track-unknown-field":
                track["fileName"] = "invented.bin";
                break;
        }

        MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChdRejectionStage.Header, "InvalidCdGeometry", MediaWorkerStatus.Unsupported)]
    [InlineData(ChdRejectionStage.Decode, "UnsupportedCdMetadata", MediaWorkerStatus.Unsupported)]
    [InlineData(ChdRejectionStage.Decode, "UnchecksummedCdMetadata", MediaWorkerStatus.Unsupported)]
    [InlineData(ChdRejectionStage.Decode, "UnsupportedCdTrackType", MediaWorkerStatus.Unsupported)]
    [InlineData(ChdRejectionStage.Decode, "UnsupportedCdSubcode", MediaWorkerStatus.Unsupported)]
    [InlineData(ChdRejectionStage.Decode, "CdMetadataLimitExceeded", MediaWorkerStatus.LimitExceeded)]
    [InlineData(ChdRejectionStage.Integrity, "DecodedMetadataMismatch", MediaWorkerStatus.Malformed)]
    public void WriteRead_CdRejection_RetainsExactCause(ChdRejectionStage stage, string cause, MediaWorkerStatus status)
    {
        var reply = Valid() with
        {
            Status = status,
            CdInspection = null,
            NativeIdentity = stage == ChdRejectionStage.Header ? null : Valid().NativeIdentity,
            ChdRejection = new(stage, cause, Path.Combine(Path.GetTempPath(), "input.chd"))
        };

        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reply);
    }
}
