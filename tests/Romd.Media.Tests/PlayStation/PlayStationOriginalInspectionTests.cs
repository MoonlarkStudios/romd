using System.Security.Cryptography;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class PlayStationOriginalInspectionTests
{
    [Theory]
    [InlineData("original.iso")]
    [InlineData("renamed.bin")]
    public async Task InspectIsoAsync_Ps2BootLayout_MeasuresOriginalAndCarriesObservationThroughProtocol(string filename)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        string directory = Directory.CreateTempSubdirectory("romd-ps2-original-").FullName;
        try
        {
            string path = Path.Combine(directory, filename);
            await File.WriteAllBytesAsync(path, bytes);

            var result = await MediaInspectionService.InspectIsoAsync(path, 1L << 20, 1L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            var measurement = result.Inspection.ShouldNotBeNull();
            measurement.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            measurement.LogicalSha256.ShouldBe(measurement.ArtifactSha256);
            measurement.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(bytes)));
            measurement.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(bytes)));
            measurement.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(bytes).ToString("x8"));
            measurement.LogicalBytes.ShouldBe(bytes.LongLength);
            measurement.ArtifactBytes.ShouldBe(bytes.LongLength);
            measurement.DiscKind.ShouldBeNull();
            measurement.EncodingProfile.ShouldBe("identity/v1");
            result.IsoInspection!.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
            var ps2 = result.Ps2Inspection.ShouldNotBeNull();
            ps2.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
            ps2.Profile.ShouldBe("ps2-iso-root-elf/v1");
            ps2.Facts!.BootSerial.ShouldBe("SLUS_203.12");
            ps2.Facts.LogicalBytes.ShouldBe(bytes.LongLength);
            ps2.Facts.BootPath.ShouldBe("cdrom0:\\SLUS_203.12;1");
            var reply = new MediaWorkerReply(result.Status,
                new(41, 41, 512L << 20, "decoder/exact", "worker/exact", null, 512L << 20, 0),
                result.Inspection, result.IsoInspection, Ps2Inspection: ps2);
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var consumed).ShouldBeTrue();
            consumed.ShouldBe(reply);
            // This small synthetic can also be a CD data image. No physical media kind is emitted.
            consumed!.Inspection!.DiscKind.ShouldBeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectIsoAsync_MissingOrContradictoryBoot2_KeepsMeasuredOriginalWithoutPositiveBootFacts(bool duplicate)
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        SyntheticPs2Iso.SetConfiguration(bytes, duplicate
            ? "BOOT2 = cdrom0:\\SLUS_203.12;1\nBOOT2 = cdrom0:\\SLUS_203.12;1\n"
            : "VER = 1.00\n");
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);

            var result = await MediaInspectionService.InspectIsoAsync(path, 1L << 20, 1L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            result.Inspection!.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            result.Inspection.LogicalSha256.ShouldBe(result.Inspection.ArtifactSha256);
            result.Ps2Inspection!.Status.ShouldBe(duplicate ? Ps2DvdIdentificationStatus.Malformed : Ps2DvdIdentificationStatus.NotIdentified);
            result.Ps2Inspection.Facts.ShouldBeNull();
        }
        finally { File.Delete(path); }
    }
}
