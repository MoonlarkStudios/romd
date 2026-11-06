using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Moonlark.Libchdr;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed partial class ChdCdInspectionServiceTests
{
    [Theory]
    [InlineData(false, 0, false, false)]
    [InlineData(true, 0, false, true)]
    [InlineData(true, 150, true, true)]
    [InlineData(true, 150, false, false)]
    public async Task InspectAsync_RawPs1WithoutCueOrDat_MeasuresBootFromTheVerifiedFirstTrack(bool mode2, int pregap, bool stored, bool withAudio)
    {
        byte[] bin = SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.Image(), mode2, stored ? pregap : 0);
        var type = mode2 ? ChdCdTrackType.Mode2Raw : ChdCdTrackType.Mode1Raw;
        byte[] audio = withAudio ? Enumerable.Range(0, 16 * 2352).Select(i => (byte)(i * 7)).ToArray() : [];
        string path = Write(BootContainer(bin, type, pregap, stored, audio));

        var result = await InspectAsync(path);

        result.Status.ShouldBe(MediaWorkerStatus.Inspected);
        var inspection = result.Inspection.ShouldNotBeNull();
        var boot = inspection.Ps1Boot.ShouldNotBeNull();
        boot.Profile.ShouldBe(Ps1CdIdentifier.Profile);
        boot.FirstTrackIndex01.ShouldBe(stored ? pregap : 0);
        boot.BootPath.ShouldBe("SLUS_000.01;1");
        boot.BootBlock.ShouldBe(26U);
        boot.EntryPoint.ShouldBe(0x80010000U);
        boot.LoadAddress.ShouldBe(0x80010000U);
        boot.LoadBytes.ShouldBe(2048U);
        boot.VolumeBlocks.ShouldBe(64U);
        boot.RootBlock.ShouldBe(24U);
        boot.RootBytes.ShouldBe(2048U);
        AssertTrack(inspection.Tracks[0], bin);
        if (withAudio)
            AssertTrack(inspection.Tracks[1], audio);
        inspection.StoredRawSha1.ShouldBe(inspection.ComputedRawSha1);
        inspection.StoredOverallSha1.ShouldBe(inspection.ComputedOverallSha1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_ForeignBootOrAudioFirst_StillMeasuresBytesWithoutPlatformClaim(bool audioFirst)
    {
        byte[] bin = SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.Image());
        if (!audioFirst)
            bin[16 * 2352 + 16 + 8] = (byte)'X';
        string path = Write(BootContainer(bin, audioFirst ? ChdCdTrackType.Audio : ChdCdTrackType.Mode1Raw, 0, false, []));

        var result = await InspectAsync(path);

        result.Status.ShouldBe(MediaWorkerStatus.Inspected);
        var inspection = result.Inspection.ShouldNotBeNull();
        inspection.Ps1Boot.ShouldBeNull();
        AssertTrack(inspection.Tracks[0], bin);
    }

    [Fact]
    public async Task InspectAsync_BootReadDiffersFromTheIntegrityPass_RefusesTheContradictoryObservation()
    {
        byte[] bin = SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.Image());
        string path = Write(BootContainer(bin, ChdCdTrackType.Mode1Raw, 0, false, []));
        // The native source lies only during the first read of the boot header's hunk. The complete
        // integrity pass sees the authentic bytes, so an artifact rehash alone cannot catch this.
        const int hunk = 4 * 2448;
        long pc = hunk + 26L * 2448 + 16 + 0x10;
        var result = await ChdCdInspectionService.InspectAsync(path, ChdNativeCandidateTests.RepositoryNative(), 2L << 30, 2L << 30,
            source => new MetadataVariant(source, pc, [0x00, 0x00, 0x02, 0x80], 1), CancellationToken.None);

        result.Status.ShouldBe(MediaWorkerStatus.Malformed);
        result.Rejection.ShouldNotBeNull().Cause.ShouldBe("DecodedBootMismatch");
        result.Rejection.Stage.ShouldBe(ChdRejectionStage.Integrity);
        result.Inspection.ShouldBeNull();
    }

    private static byte[] BootContainer(byte[] bin, ChdCdTrackType type, int pregap, bool stored, byte[] audio)
    {
        const int hunk = 4 * 2448;
        string name = type switch { ChdCdTrackType.Mode1Raw => "MODE1_RAW", ChdCdTrackType.Mode2Raw => "MODE2_RAW", ChdCdTrackType.Audio => "AUDIO", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
        int firstFrames = bin.Length / 2352, firstPadded = (firstFrames + 3) / 4 * 4;
        int audioFrames = audio.Length / 2352, totalFrames = firstPadded + (audioFrames + 3) / 4 * 4;
        byte[] logical = new byte[totalFrames * 2448];
        for (int i = 0; i < firstFrames; i++)
        {
            var sector = logical.AsSpan(i * 2448, 2352);
            bin.AsSpan(i * 2352, 2352).CopyTo(sector);
            if (type == ChdCdTrackType.Audio)
                ChdCdImage.SwapAudioSamples16(sector);
        }
        for (int i = 0; i < audioFrames; i++)
        {
            var sector = logical.AsSpan((firstPadded + i) * 2448, 2352);
            audio.AsSpan(i * 2352, 2352).CopyTo(sector);
            ChdCdImage.SwapAudioSamples16(sector);
        }
        string[] descriptions = audio.Length == 0
            ? [$"TRACK:1 TYPE:{name} SUBTYPE:NONE FRAMES:{firstFrames} PREGAP:{pregap} PGTYPE:{(stored ? "V" : "")}{name} PGSUB:NONE POSTGAP:0\0"]
            : [$"TRACK:1 TYPE:{name} SUBTYPE:NONE FRAMES:{firstFrames} PREGAP:{pregap} PGTYPE:{(stored ? "V" : "")}{name} PGSUB:NONE POSTGAP:0\0",
                $"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:{audioFrames} PREGAP:0 PGTYPE:AUDIO PGSUB:NONE POSTGAP:0\0"];
        byte[][] metadata = descriptions.Select(Encoding.ASCII.GetBytes).ToArray();
        int hunks = logical.Length / hunk;
        byte[] artifact = new byte[(hunks + 1) * hunk];
        "MComprHD"u8.CopyTo(artifact);
        BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(artifact.AsSpan(32), (ulong)logical.Length);
        BinaryPrimitives.WriteUInt64BigEndian(artifact.AsSpan(40), 124);
        BinaryPrimitives.WriteUInt64BigEndian(artifact.AsSpan(48), (ulong)(124 + hunks * 4));
        BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(56), hunk);
        BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(60), 2448);
        for (int i = 0; i < hunks; i++)
            BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(124 + i * 4), (uint)i + 1);
        int at = 124 + hunks * 4;
        byte[][] records = new byte[metadata.Length][];
        for (int i = 0; i < metadata.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(at), ChdMetadataTag.CdTrackV2.Value);
            BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(at + 4), 0x01000000U | (uint)metadata[i].Length);
            BinaryPrimitives.WriteUInt64BigEndian(artifact.AsSpan(at + 8), i + 1 == metadata.Length ? 0UL : (ulong)(at + 16 + metadata[i].Length));
            metadata[i].CopyTo(artifact, at + 16);
            at += 16 + metadata[i].Length;
            records[i] = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(records[i], ChdMetadataTag.CdTrackV2.Value);
            SHA1.HashData(metadata[i]).CopyTo(records[i], 4);
        }
        at.ShouldBeLessThan(hunk);
        logical.CopyTo(artifact, hunk);
        byte[] raw = SHA1.HashData(logical);
        raw.CopyTo(artifact, 64);
        Array.Sort(records, (left, right) => left.AsSpan().SequenceCompareTo(right));
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        overall.AppendData(raw);
        foreach (byte[] record in records)
            overall.AppendData(record);
        overall.GetHashAndReset().CopyTo(artifact, 84);
        return artifact;
    }
}
