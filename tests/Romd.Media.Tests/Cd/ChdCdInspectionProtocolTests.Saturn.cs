using System.Text;
using System.Text.Json.Nodes;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Cd;

public sealed partial class ChdCdInspectionProtocolTests
{
    private static MediaWorkerReply SaturnReply(bool job)
    {
        var original = BootReply(job);
        var before = original.CdInspection!;
        var tracks = new[] { before.Tracks[0] with { Type = Moonlark.Libchdr.ChdCdTrackType.Mode1Raw }, before.Tracks[1] };
        var saturn = new SaturnCdObservedFacts(SaturnCdIdentifier.Profile, 0, "T-00001", "JUE", 4096, new string('a', 64));
        return original with
        {
            CdInspection = before with { Ps1Boot = null, SaturnBoot = saturn, Tracks = tracks, Metadata = tracks.Select(Metadata).ToArray() },
            CdVerification = job ? original.CdVerification! with { Ps1Boot = null, SaturnBoot = saturn } : null
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRead_SaturnObservation_RetainsDistinctFactsAndRefusesContradictions(bool job)
    {
        var reply = SaturnReply(job);
        byte[] wire = MediaWorkerProtocol.Write(reply);
        MediaWorkerProtocol.TryRead(wire, out var parsed).ShouldBeTrue();
        parsed!.CdInspection!.ObservedBoot.ShouldBe(reply.CdInspection!.ObservedBoot);
        parsed.CdInspection.Ps1Boot.ShouldBeNull();
        foreach (string mutation in new[] { "dual-family", "unknown-field", "missing-field", "header", "extent", "profile", "mode" })
        {
            var root = JsonNode.Parse(wire)!;
            var inspection = root[job ? "inspection" : "cdInspection"]!;
            var boot = inspection["saturnBoot"]!;
            switch (mutation)
            {
                case "dual-family":
                    inspection["ps1Boot"] = JsonNode.Parse(MediaWorkerProtocol.Write(BootReply(job)))![job ? "inspection" : "cdInspection"]!["ps1Boot"]!.DeepClone();
                    break;
                case "unknown-field":
                    boot["extra"] = true;
                    break;
                case "missing-field":
                    inspection.AsObject().Remove("saturnBoot");
                    break;
                case "header":
                    boot["headerSha256"] = new string('0', 64);
                    break;
                case "extent":
                    boot["firstTrackIndex01"] = 63;
                    break;
                case "profile":
                    boot["profile"] = Ps1CdIdentifier.Profile;
                    break;
                case "mode":
                    inspection["tracks"]![0]!["type"] = "audio";
                    break;
            }
            MediaWorkerProtocol.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString()), out _).ShouldBeFalse(mutation);
        }
    }
}
