using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Moonlark.Libchdr;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed partial class ChdCdInspectionServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-cd-inspection-").FullName;

    [Theory]
    [InlineData("cdlz")]
    [InlineData("cdzl")]
    [InlineData("cdfl")]
    [InlineData("cdzs")]
    public async Task InspectAsync_CdCodecs_MeasuresDataAndLittleEndianAudioAgainstSource(string codec)
    {
        string path = Fixture("cd-" + codec + ".chd");
        var result = await InspectAsync(path);

        AssertIntegrity(result, path, Fixture("cd-" + codec + ".logical"));
        byte[] bin = File.ReadAllBytes(Fixture("cd-source.bin"));
        result.Inspection!.Tracks.Count.ShouldBe(2);
        AssertTrack(result.Inspection.Tracks[0], bin[..(16 * 2352)]);
        AssertTrack(result.Inspection.Tracks[1], bin[(16 * 2352)..]);
        result.Inspection.Tracks[1].PregapStored.ShouldBeTrue();
        result.Inspection.Tracks[1].PregapFrames.ShouldBe(4U);
    }

    [Theory]
    [InlineData("multi", 3)]
    [InlineData("single", 3)]
    [InlineData("partial", 2)]
    public async Task InspectAsync_TrackBoundaries_PreservesStoredPregapAndOmitsAlignment(string shape, int tracks)
    {
        string path = Fixture("cd-edge-" + shape + ".chd");
        var result = await InspectAsync(path);

        AssertIntegrity(result, path, Fixture("cd-edge-" + shape + ".logical"));
        result.Inspection!.Tracks.Count.ShouldBe(tracks);
        for (int index = 0; index < tracks; index++)
            AssertTrack(result.Inspection.Tracks[index], File.ReadAllBytes(Fixture($"cd-edge-track{index + 1}.bin")));
        result.Inspection.Tracks[0].Type.ShouldBe(ChdCdTrackType.Mode2Raw);
        result.Inspection.Tracks[0].AlignmentFrames.ShouldBe(3U);
        result.Inspection.Tracks[1].FirstFrame.ShouldBe(20UL);
        result.Inspection.Tracks[1].PregapFrames.ShouldBe(2U);
        result.Inspection.Tracks[1].PregapStored.ShouldBeTrue();
        if (tracks == 3)
        {
            result.Inspection.Tracks[2].PregapFrames.ShouldBe(2U);
            result.Inspection.Tracks[2].PregapStored.ShouldBeFalse();
        }
        // The same logical tracks have identical projections for one source BIN and separate source BINs.
        // Neither case claims that a projected track was an original standalone file.
    }

    [Theory]
    [InlineData("signature", MediaWorkerStatus.Malformed, "InvalidSignature")]
    [InlineData("version", MediaWorkerStatus.Unsupported, "UnsupportedVersion")]
    [InlineData("parent", MediaWorkerStatus.Unsupported, "ParentReference")]
    [InlineData("geometry", MediaWorkerStatus.Unsupported, "InvalidCdGeometry")]
    [InlineData("logical-limit", MediaWorkerStatus.LimitExceeded, "LogicalLimitExceeded")]
    [InlineData("hunk-limit", MediaWorkerStatus.LimitExceeded, "HunkLimitExceeded")]
    public async Task InspectAsync_HeaderRejection_PrecedesNativeAccess(string mutation, MediaWorkerStatus status, string cause)
    {
        byte[] bytes = File.ReadAllBytes(Fixture("cd-cdlz.chd"));
        switch (mutation)
        {
            case "signature":
                bytes[0] ^= 1;
                break;
            case "version":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 4);
                break;
            case "parent":
                bytes[104] = 1;
                break;
            case "geometry":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), 2048);
                break;
            case "logical-limit":
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), (ulong)ChdCdLimits.MaximumLogicalBytes + 2448);
                break;
            case "hunk-limit":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), ChdCdLimits.MaximumHunkBytes + 2448);
                break;
        }
        var missing = new ChdNativeCandidate(Path.Combine(_directory, "missing"), ChdNativeCandidateTests.PrototypeIdentity());

        var result = await ChdCdInspectionService.InspectAsync(Write(bytes), missing, 2L << 30, 2L << 30);

        result.Status.ShouldBe(status);
        result.Rejection!.Stage.ShouldBe(ChdRejectionStage.Header);
        result.Rejection.Cause.ShouldBe(cause);
        result.NativeIdentity.ShouldBeNull();
        result.Inspection.ShouldBeNull();
    }

    [Theory]
    [InlineData("raw-missing", "MissingRawSha1")]
    [InlineData("raw-mismatch", "RawSha1Mismatch")]
    [InlineData("overall-missing", "MissingOverallSha1")]
    [InlineData("overall-mismatch", "OverallSha1Mismatch")]
    public async Task InspectAsync_BrokenIntegrity_ReturnsNoTrackMeasurements(string mutation, string cause)
    {
        byte[] bytes = File.ReadAllBytes(Fixture("cd-edge-multi.chd"));
        int at = mutation.StartsWith("raw", StringComparison.Ordinal) ? 64 : 84;
        if (mutation.EndsWith("missing", StringComparison.Ordinal))
            bytes.AsSpan(at, 20).Clear();
        else
            bytes[at] ^= 1;

        var result = await InspectAsync(Write(bytes));

        result.Status.ShouldBe(MediaWorkerStatus.Malformed);
        result.Rejection!.Stage.ShouldBe(ChdRejectionStage.Integrity);
        result.Rejection.Cause.ShouldBe(cause);
        result.NativeIdentity.ShouldNotBeNull();
        result.Inspection.ShouldBeNull();
    }

    [Theory]
    [InlineData("subcode", MediaWorkerStatus.Unsupported, "UnsupportedCdSubcode")]
    [InlineData("cooked", MediaWorkerStatus.Unsupported, "UnsupportedCdTrackType")]
    [InlineData("foreign", MediaWorkerStatus.Unsupported, "UnsupportedCdMetadata")]
    [InlineData("unchecked", MediaWorkerStatus.Unsupported, "UnchecksummedCdMetadata")]
    [InlineData("too-many", MediaWorkerStatus.LimitExceeded, "CdMetadataLimitExceeded")]
    [InlineData("gap-limit", MediaWorkerStatus.LimitExceeded, "CdMetadataLimitExceeded")]
    [InlineData("duplicate", MediaWorkerStatus.Malformed, "NativeDecodeFailure")]
    [InlineData("missing-track", MediaWorkerStatus.Malformed, "NativeDecodeFailure")]
    [InlineData("coverage", MediaWorkerStatus.Malformed, "NativeDecodeFailure")]
    public async Task InspectAsync_UnsupportedOrMalformedLayout_HasConcreteRejection(string mutation, MediaWorkerStatus status, string cause)
    {
        byte[] bytes = File.ReadAllBytes(Fixture("cd-edge-multi.chd"));
        string[] tracks = [Metadata(1, 17, "MODE2_RAW"), Metadata(2, 13, "AUDIO"), Metadata(3, 9, "AUDIO")];
        uint tag = ChdMetadataTag.CdTrackV2.Value;
        byte flags = 1;
        switch (mutation)
        {
            case "subcode":
                tracks[0] = Metadata(1, 17, "MODE2_RAW", subtype: "RW_RAW");
                break;
            case "cooked":
                tracks[0] = Metadata(1, 17, "MODE1");
                break;
            case "foreign":
                tag = ChdMetadataTag.GdTrack.Value;
                break;
            case "unchecked":
                flags = 0;
                break;
            case "too-many":
                tracks = Enumerable.Range(1, 100).Select(_ => Metadata(1, 1, "AUDIO")).ToArray();
                break;
            case "gap-limit":
                tracks[0] = Metadata(1, 17, "MODE2_RAW", postgap: ChdCdLimits.MaximumFrames + 1);
                break;
            case "duplicate":
                tracks[1] = Metadata(1, 13, "AUDIO");
                break;
            case "missing-track":
                tracks[1] = Metadata(4, 13, "AUDIO");
                break;
            case "coverage":
                tracks[0] = Metadata(1, 21, "MODE2_RAW");
                break;
        }

        var result = await InspectAsync(Write(WithMetadata(bytes, tag, flags, tracks)));

        result.Status.ShouldBe(status);
        result.Rejection!.Stage.ShouldBe(ChdRejectionStage.Decode);
        result.Rejection.Cause.ShouldBe(cause);
        result.Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_Dvd_DeclinesBeforeNativeAccess()
    {
        var result = await InspectAsync(Fixture("dvd-zstd.chd"));
        result.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        result.Rejection!.Cause.ShouldBe("InvalidCdGeometry");
        result.NativeIdentity.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_PreCancelled_DoesNotOpenInput()
    {
        await Should.ThrowAsync<OperationCanceledException>(() => ChdCdInspectionService.InspectAsync(
            Path.Combine(_directory, "missing.chd"), ChdNativeCandidateTests.RepositoryNative(), 2L << 30, 2L << 30,
            new CancellationToken(true)));
    }

    [Fact]
    public async Task InspectAsync_CancelledDuringNativeRead_DoesNotPublishPartialMeasurement()
    {
        using var cancellation = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => ChdCdInspectionService.InspectAsync(
            Fixture("cd-edge-multi.chd"), ChdNativeCandidateTests.RepositoryNative(), 2L << 30, 2L << 30,
            source => new CancelOnRead(source, cancellation), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_ParsedMetadataDiffersFromVerifiedMetadata_RejectsContradictoryTrackEvidence(bool alterCapturedPayload)
    {
        string path = Fixture("cd-edge-multi.chd");
        byte[] artifact = File.ReadAllBytes(path);
        int at = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(48)));
        int length = (int)(BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(at + 4)) & 0xffffff);
        byte[] variant = artifact.AsSpan(at + 16, length).ToArray();
        int typeAt = variant.AsSpan().IndexOf(alterCapturedPayload ? "PGTYPE:MODE1"u8 : "MODE2_RAW"u8);
        typeAt.ShouldBeGreaterThanOrEqualTo(0);
        if (alterCapturedPayload)
            "PGTYPE:MODE2"u8.CopyTo(variant.AsSpan(typeAt));
        else
            "MODE1_RAW"u8.CopyTo(variant.AsSpan(typeAt));

        // The first control substitutes a valid MODE1 layout only during parsing. The second changes an
        // ignored pregap type only during payload capture; the semantic table agrees, but its payload hash
        // does not. Both must be bound to the exact metadata whose overall hash is verified.
        var result = await ChdCdInspectionService.InspectAsync(path, ChdNativeCandidateTests.RepositoryNative(),
            2L << 30, 2L << 30, source => new MetadataVariant(source, at + 16, variant, alterCapturedPayload ? 2 : 1), CancellationToken.None);

        result.Status.ShouldBe(MediaWorkerStatus.Malformed);
        result.Rejection!.Stage.ShouldBe(ChdRejectionStage.Integrity);
        result.Rejection.Cause.ShouldBe("DecodedMetadataMismatch");
        result.Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_MissingInput_ReturnsIoFailure()
    {
        var result = await InspectAsync(Path.Combine(_directory, "missing.chd"));
        result.Status.ShouldBe(MediaWorkerStatus.IoFailure);
        result.Rejection!.Cause.ShouldBe("SourceIo");
        result.Inspection.ShouldBeNull();
    }

    private static void AssertIntegrity(ChdCdInspectionOutcome result, string artifactPath, string logicalPath)
    {
        result.Status.ShouldBe(MediaWorkerStatus.Inspected);
        result.Rejection.ShouldBeNull();
        var measured = result.Inspection.ShouldNotBeNull();
        measured.Version.ShouldBe(5U);
        measured.HasParent.ShouldBeFalse();
        measured.Profile.ShouldBe(ChdCdInspectionService.Profile);
        measured.ProjectionProfile.ShouldBe(ChdCdInspectionService.ProjectionProfile);
        measured.ArtifactSha256.ShouldBe(Sha256(File.ReadAllBytes(artifactPath)));
        measured.Header.ArtifactBytes.ShouldBe(new FileInfo(artifactPath).Length);
        measured.Header.LogicalBytes.ShouldBe((ulong)new FileInfo(logicalPath).Length);
        measured.ComputedRawSha1.ShouldBe(Sha1(File.ReadAllBytes(logicalPath)));
        measured.StoredRawSha1.ShouldBe(measured.ComputedRawSha1);
        measured.StoredOverallSha1.ShouldBe(measured.ComputedOverallSha1);
        measured.Tracks.Sum(track => track.BinBytes).ShouldBeLessThan((long)measured.Header.LogicalBytes);
        // Compare the exact bytes against an independent walk of the fixture's on-disk metadata chain.
        byte[] artifact = File.ReadAllBytes(artifactPath);
        ulong next = BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(48));
        measured.Metadata.Count.ShouldBe(measured.Tracks.Count);
        foreach (var metadata in measured.Metadata)
        {
            int offset = checked((int)next);
            uint packedLength = BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(offset + 4));
            metadata.Tag.ShouldBe(BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(offset)));
            metadata.Flags.ShouldBe((byte)(packedLength >> 24));
            metadata.PayloadHex.ShouldBe(Convert.ToHexStringLower(artifact.AsSpan(offset + 16, (int)(packedLength & 0xffffff))));
            next = BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(offset + 8));
        }
        next.ShouldBe(0UL);
    }

    private static void AssertTrack(ChdCdTrackInspection actual, byte[] expected)
    {
        actual.BinBytes.ShouldBe(expected.LongLength);
        actual.Sha256.ShouldBe(Sha256(expected));
        actual.Sha1.ShouldBe(Sha1(expected));
        actual.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(expected)));
        actual.Crc32.ShouldBe(Crc32.HashToUInt32(expected).ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static Task<ChdCdInspectionOutcome> InspectAsync(string path) => ChdCdInspectionService.InspectAsync(
        path, ChdNativeCandidateTests.RepositoryNative(), 2L << 30, 2L << 30);
    private static string Fixture(string name) => NativeTestBuilds.LibchdrFixture(name);
    private string Write(byte[] bytes)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".chd");
        File.WriteAllBytes(path, bytes);
        return path;
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
    private static string Metadata(int number, int frames, string type, string subtype = "NONE", uint postgap = 0) =>
        $"TRACK:{number} TYPE:{type} SUBTYPE:{subtype} FRAMES:{frames} PREGAP:0 PGTYPE:{type} PGSUB:NONE POSTGAP:{postgap}\0";

    private static byte[] WithMetadata(byte[] original, uint tag, byte flags, string[] payloads)
    {
        byte[][] data = payloads.Select(Encoding.ASCII.GetBytes).ToArray();
        byte[] result = new byte[original.Length + data.Sum(bytes => 16 + bytes.Length)];
        original.CopyTo(result, 0);
        int at = original.Length;
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(48), (ulong)at);
        for (int index = 0; index < data.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at + 4), ((uint)flags << 24) | (uint)data[index].Length);
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(at + 8), index + 1 == data.Length ? 0 : (ulong)(at + 16 + data[index].Length));
            data[index].CopyTo(result, at + 16);
            at += 16 + data[index].Length;
        }
        return result;
    }

    private sealed class CancelOnRead(IChdDataSource source, CancellationTokenSource cancellation) : IChdDataSource
    {
        public long Length => source.Length;
        public int Read(long offset, Span<byte> destination)
        {
            int count = source.Read(offset, destination);
            cancellation.Cancel();
            return count;
        }
        public void Dispose() => source.Dispose();
    }

    private sealed class MetadataVariant(IChdDataSource source, long start, byte[] variant, int selectedRead) : IChdDataSource
    {
        private int _reads;
        public long Length => source.Length;
        public int Read(long offset, Span<byte> destination)
        {
            int count = source.Read(offset, destination);
            long from = Math.Max(offset, start), to = Math.Min(offset + count, start + variant.Length);
            if (from >= to || ++_reads != selectedRead)
                return count;
            variant.AsSpan((int)(from - start), (int)(to - from)).CopyTo(destination[(int)(from - offset)..]);
            return count;
        }
        public void Dispose() => source.Dispose();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
