using System.Buffers.Binary;
using System.Security.Cryptography;
using Moonlark.Libchdr;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Chd;

public sealed class ChdInspectionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "romd-chd-inspection-" + Guid.NewGuid().ToString("N"));

    public ChdInspectionServiceTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("truncated", "TruncatedHeader", MediaWorkerStatus.Malformed)]
    [InlineData("signature", "InvalidSignature", MediaWorkerStatus.Malformed)]
    [InlineData("header-length", "InvalidHeaderLength", MediaWorkerStatus.Malformed)]
    [InlineData("version", "UnsupportedVersion", MediaWorkerStatus.Unsupported)]
    [InlineData("parent", "ParentReference", MediaWorkerStatus.Unsupported)]
    [InlineData("zero-logical", "ZeroLogicalBytes", MediaWorkerStatus.Malformed)]
    [InlineData("logical-limit", "LogicalLimitExceeded", MediaWorkerStatus.LimitExceeded)]
    [InlineData("hunk-limit", "HunkLimitExceeded", MediaWorkerStatus.LimitExceeded)]
    [InlineData("dvd-unit", "InvalidDvdGeometry", MediaWorkerStatus.Unsupported)]
    [InlineData("dvd-logical", "InvalidDvdGeometry", MediaWorkerStatus.Unsupported)]
    [InlineData("dvd-hunk", "InvalidDvdGeometry", MediaWorkerStatus.Unsupported)]
    public async Task InspectAsync_RejectedHeader_PrecedesAnyNativeFileAccess(string mutation, string cause, MediaWorkerStatus status)
    {
        byte[] header = Header();
        switch (mutation)
        {
            case "truncated":
                header = header[..123];
                break;
            case "signature":
                header[0] ^= 1;
                break;
            case "header-length":
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 123);
                break;
            case "version":
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 4);
                break;
            case "parent":
                header[123] = 1;
                break;
            case "zero-logical":
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), 0);
                break;
            case "logical-limit":
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), (16UL << 20) + 2048);
                break;
            case "hunk-limit":
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), (1U << 20) + 1);
                break;
            case "dvd-unit":
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), 512);
                break;
            case "dvd-logical":
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), 8193);
                break;
            case "dvd-hunk":
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 4097);
                break;
        }
        string path = Write(header);

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(path, MissingCandidate(), 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(status);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Header);
        outcome.Rejection.Cause.ShouldBe(cause);
        outcome.Rejection.Path.ShouldBe(path);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.NativeIdentity.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, (uint)ChdCodec.CdZlib)]
    [InlineData(1, (uint)ChdCodec.CdLzma)]
    [InlineData(2, (uint)ChdCodec.CdFlac)]
    [InlineData(3, (uint)ChdCodec.CdZstd)]
    public async Task InspectAsync_DvdWithCdCodec_ReturnsExactSlotBeforeNativeAccess(int slot, uint codec)
    {
        byte[] header = Header();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16 + slot * 4), codec);

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(header), MissingCandidate(), 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        outcome.Rejection!.Cause.ShouldBe("UnsupportedDvdCodec");
        outcome.Rejection.CodecSlot.ShouldBe(slot);
        outcome.Rejection.Codec.ShouldBe(codec);
        outcome.NativeIdentity.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_ValidHeader_ReachesExplicitNativeVerification()
    {
        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(Header()), MissingCandidate(), 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.NativeVerification);
        outcome.Rejection.Cause.ShouldBe("ManifestMissing");
        outcome.Inspection.ShouldBeNull();
    }

    [Theory]
    [InlineData(4L, 8192L, "ArtifactLimitExceeded")]
    [InlineData(123L, 8192L, "ArtifactLimitExceeded")]
    [InlineData(124L, 0x81L, "LogicalLimitExceeded")]
    [InlineData(124L, 2047L, "LogicalLimitExceeded")]
    public async Task InspectAsync_SmallValidProcessingBudget_ReturnsHeaderLimitBeforeNativeAccess(long artifactLimit, long logicalLimit, string cause)
    {
        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(Header()), MissingCandidate(), artifactLimit, logicalLimit);

        outcome.Status.ShouldBe(MediaWorkerStatus.LimitExceeded);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Header);
        outcome.Rejection.Cause.ShouldBe(cause);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.NativeIdentity.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_Cancelled_PropagatesBeforeMissingInputAccess()
    {
        await Should.ThrowAsync<OperationCanceledException>(() => ChdInspectionService.InspectAsync(
            Path.Combine(_directory, "absent"), MissingCandidate(), 16L << 20, 16L << 20, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task InspectAsync_MissingSource_ReturnsTypedIoFailure()
    {
        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Path.Combine(_directory, "absent"), MissingCandidate(), 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.IoFailure);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Io);
        outcome.Rejection.Cause.ShouldBe("SourceIo");
        outcome.NativeIdentity.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_ClippedFinalHunk_MatchesIndependentFourDigestsAndArtifact()
    {
        var (artifact, logical, _) = Fixture();
        artifact = WithMetadata(artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Inspected);
        outcome.Rejection.ShouldBeNull();
        MediaInspection measured = outcome.Inspection!;
        measured.ArtifactBytes.ShouldBe(artifact.LongLength);
        measured.ArtifactSha256.ShouldBe(Digest(artifact));
        measured.LogicalBytes.ShouldBe(logical.LongLength);
        measured.LogicalSha256.ShouldBe(Digest(logical));
        measured.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(logical)));
        measured.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(logical)));
        measured.Crc32.ShouldBe(Crc(logical));
        measured.EncodingProfile.ShouldBe("chd-import/v1");
        measured.DiscKind.ShouldBeNull();
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.ChdInspection!.Header.HunkCount.ShouldBe(3U);
        outcome.ChdInspection.StoredRawSha1.ShouldBe(measured.Sha1);
        outcome.ChdInspection.StoredOverallSha1.ShouldBe(outcome.ChdInspection.ComputedOverallSha1);
        outcome.ChdInspection.DvdMetadata.ShouldBe(ExpectedDvdMetadata());
        outcome.Ps2Inspection!.Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
        outcome.Ps2Inspection.Facts.ShouldBeNull();
    }

    [Theory]
    [InlineData("raw", "RawSha1Mismatch")]
    [InlineData("overall", "OverallSha1Mismatch")]
    [InlineData("missing-raw", "MissingRawSha1")]
    [InlineData("missing-overall", "MissingOverallSha1")]
    public async Task InspectAsync_StoredHashControl_ReturnsExactIntegrityRejection(string mutation, string cause)
    {
        var (artifact, _, _) = Fixture();
        artifact = WithMetadata(artifact, DvdMarker());
        if (mutation == "raw")
            artifact[64] ^= 1;
        else if (mutation == "overall")
            artifact[84] ^= 1;
        else
            artifact.AsSpan(mutation == "missing-raw" ? 64 : 84, 20).Clear();
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection!.Cause.ShouldBe(cause);
        outcome.Rejection.Stage.ShouldBe(ChdRejectionStage.Integrity);
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.Ps2Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_DecodedBytesDifferFromStoredRawSha1_ReturnsRawSha1Mismatch()
    {
        var (_, logical, _) = Fixture();
        byte[] changed = logical.ToArray();
        changed[5000] ^= 1;
        // Valid hunk CRCs over the changed bytes: decoding succeeds and only the computed raw SHA-1 diverges.
        var (artifact, _) = StoredHunkFixture(changed);
        SHA1.HashData(logical).CopyTo(artifact, 64);
        artifact = WithMetadata(artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Integrity);
        outcome.Rejection.Cause.ShouldBe("RawSha1Mismatch");
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.Ps2Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_IdentifierDecodeDiffersFromVerifiedPass_RejectsDecodedHunkMismatch()
    {
        const int hunkBytes = 4096;
        byte[] logical = SyntheticPs2Iso.Create();
        var (stored, payload) = StoredHunkFixture(logical, hunkBytes: hunkBytes);
        byte[] artifact = WithMetadata(stored, DvdMarker());
        long bootHunk = payload + (long)(SyntheticPs2Iso.BootExecutableOffset / hunkBytes) * hunkBytes;
        // A same-CRC16 variant of the boot executable's hunk decodes without a native error, so only comparing what
        // PS2 identification decoded with what the verified pass decoded can reveal it.
        byte[] variant = SameCrc16Variant(artifact.AsSpan((int)bootHunk, hunkBytes).ToArray());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20,
            source => new FirstReadVariantSource(source, bootHunk, variant), CancellationToken.None);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Integrity);
        outcome.Rejection.Cause.ShouldBe("DecodedHunkMismatch");
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.Ps2Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_ValidDvd_DecodesEachStoredHunkOnce()
    {
        var (artifact, logical, payload) = Fixture();
        artifact = WithMetadata(artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();
        RecordingSource? recorded = null;

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20,
            source => recorded = new RecordingSource(source), CancellationToken.None);

        AssertMeasurement(outcome, artifact, logical, candidate);
        // A stored hunk decodes by reading its payload; this fixture is below the PS2 identifier's 17-sector floor.
        long[] payloadBytesRead = Enumerable.Range(0, 3).Select(hunk => recorded.ShouldNotBeNull().Reads
            .Sum(read => Overlap(read.Offset, read.Bytes, payload + hunk * 4096L, 4096))).ToArray();
        payloadBytesRead.ShouldBe([4096L, 4096L, 4096L]);
    }

    [Fact]
    public async Task InspectAsync_CorruptStoredHunkCrc_ReturnsNativeDecodeFailureWithoutMeasurement()
    {
        var (artifact, _, payload) = Fixture();
        artifact = WithMetadata(artifact, DvdMarker());
        artifact[payload] ^= 1;
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection!.Stage.ShouldBe(ChdRejectionStage.Decode);
        outcome.Rejection.Cause.ShouldBe("NativeDecodeFailure");
        outcome.Rejection.NativeError.ShouldBe((int)ChdError.DecompressionError);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.Ps2Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_NoMetadataOriginalFixture_ReturnsMetadataRejectionWithoutMeasurement()
    {
        var (artifact, _, _) = Fixture();
        await AssertMetadataRejectedAsync(artifact);
    }

    [Theory]
    [InlineData("duplicate-dvd")]
    [InlineData("cd-chcd")]
    [InlineData("cd-chtr")]
    [InlineData("cd-cht2")]
    [InlineData("gd-chgt")]
    [InlineData("gd-chgd")]
    [InlineData("unknown")]
    [InlineData("dvd-and-checksummed-unknown")]
    [InlineData("dvd-and-unchecksummed-unknown")]
    [InlineData("payload-one")]
    [InlineData("payload-two-bytes")]
    [InlineData("payload-empty")]
    [InlineData("flags-zero")]
    [InlineData("flags-three")]
    [InlineData("flags-high-bit")]
    [InlineData("flags-high-bit-and-checksum")]
    public async Task InspectAsync_CoherentInvalidDvdMetadata_ReturnsPreciseMetadataRejection(string mutation)
    {
        MetadataEntry[] entries = mutation switch
        {
            "duplicate-dvd" => [DvdMarker(), DvdMarker()],
            "cd-chcd" => [new(0x43484344, 1, [0])],
            "cd-chtr" => [new(0x43485452, 1, [0])],
            "cd-cht2" => [new(0x43485432, 1, [0])],
            "gd-chgt" => [new(0x43484754, 1, [0])],
            "gd-chgd" => [new(0x43484744, 1, [0])],
            "unknown" => [new(0x41424344, 1, [0])],
            "dvd-and-checksummed-unknown" => [DvdMarker(), new(0x41424344, 1, [0])],
            "dvd-and-unchecksummed-unknown" => [DvdMarker(), new(0x41424344, 0, [0])],
            "payload-one" => [new(0x44564420, 1, [1])],
            "payload-two-bytes" => [new(0x44564420, 1, [0, 0])],
            "payload-empty" => [new(0x44564420, 1, [])],
            "flags-zero" => [new(0x44564420, 0, [0])],
            "flags-three" => [new(0x44564420, 3, [0])],
            "flags-high-bit" => [new(0x44564420, 128, [0])],
            "flags-high-bit-and-checksum" => [new(0x44564420, 129, [0])],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var (artifact, _, _) = Fixture();
        await AssertMetadataRejectedAsync(WithMetadata(artifact, entries));
    }

    [Fact]
    public async Task InspectAsync_SingletonDvdMetadata_ReturnsExactNormalizedObservation()
    {
        var (artifact, logical, _) = Fixture();
        artifact = WithMetadata(artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        AssertMeasurement(outcome, artifact, logical, candidate);
        outcome.ChdInspection!.DvdMetadata.ShouldBe(ExpectedDvdMetadata());
        outcome.Ps2Inspection!.Status.ShouldBe(Ps2DvdIdentificationStatus.NotIdentified);
        outcome.Ps2Inspection.Facts.ShouldBeNull();
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(6144)]
    [InlineData(16384)]
    public async Task InspectAsync_NativeDecodedPs2Boot_ReturnsBorrowedLogicalBootFacts(int hunkBytes)
    {
        byte[] logical = SyntheticPs2Iso.Create();
        byte[] artifact = WithMetadata(StoredHunkFixture(logical, hunkBytes: hunkBytes).Artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        AssertMeasurement(outcome, artifact, logical, candidate);
        outcome.ChdInspection!.DvdMetadata.ShouldBe(ExpectedDvdMetadata());
        outcome.Ps2Inspection!.Profile.ShouldBe("ps2-iso-root-elf/v1");
        outcome.Ps2Inspection.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        outcome.Ps2Inspection.Facts.ShouldBe(new Ps2DvdObservedFacts(logical.LongLength,
            SyntheticPs2Iso.VolumeBlocks, SyntheticPs2Iso.PrimaryDescriptorBlock,
            SyntheticPs2Iso.RootDirectoryBlock, SyntheticPs2Iso.SectorBytes,
            SyntheticPs2Iso.SystemCnfBlock, (uint)System.Text.Encoding.ASCII.GetByteCount(SyntheticPs2Iso.BootConfiguration),
            SyntheticPs2Iso.BootExecutableBlock, SyntheticPs2Iso.BootExecutableBytes,
            SyntheticPs2Iso.BootPath, SyntheticPs2Iso.BootPath, SyntheticPs2Iso.BootSerial, SyntheticPs2Iso.EntryPoint, 1));
    }

    [Theory]
    [InlineData(6144)]
    [InlineData(16384)]
    public async Task InspectAsync_SystemCnfSpansMultipleHunks_PreservesExactBootAndMeasurement(int hunkBytes)
    {
        byte[] logical = SyntheticPs2Iso.Create();
        const uint bootBlock = 36, configurationBytes = 16 << 10;
        logical.AsSpan(SyntheticPs2Iso.BootExecutableOffset, SyntheticPs2Iso.BootExecutableBytes)
            .CopyTo(logical.AsSpan((int)bootBlock * SyntheticPs2Iso.SectorBytes));
        byte[] configuration = System.Text.Encoding.ASCII.GetBytes(SyntheticPs2Iso.BootConfiguration.PadRight((int)configurationBytes));
        configuration.CopyTo(logical, SyntheticPs2Iso.SystemCnfOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(SyntheticPs2Iso.BootRecordOffset + 2), bootBlock);
        BinaryPrimitives.WriteUInt32BigEndian(logical.AsSpan(SyntheticPs2Iso.BootRecordOffset + 6), bootBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(SyntheticPs2Iso.SystemCnfRecordOffset + 10), configurationBytes);
        BinaryPrimitives.WriteUInt32BigEndian(logical.AsSpan(SyntheticPs2Iso.SystemCnfRecordOffset + 14), configurationBytes);
        (SyntheticPs2Iso.SystemCnfOffset / hunkBytes).ShouldNotBe(
            (SyntheticPs2Iso.SystemCnfOffset + (int)configurationBytes - 1) / hunkBytes);
        byte[] artifact = WithMetadata(StoredHunkFixture(logical, hunkBytes: hunkBytes).Artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        AssertMeasurement(outcome, artifact, logical, candidate);
        outcome.Ps2Inspection!.Status.ShouldBe(Ps2DvdIdentificationStatus.Identified);
        outcome.Ps2Inspection.Facts!.SystemCnfBytes.ShouldBe(configurationBytes);
        outcome.Ps2Inspection.Facts.BootExecutableBlock.ShouldBe(bootBlock);
        outcome.Ps2Inspection.Facts.BootSerial.ShouldBe(SyntheticPs2Iso.BootSerial);
        outcome.Ps2Inspection.Facts.EntryPoint.ShouldBe(SyntheticPs2Iso.EntryPoint);
    }

    [Theory]
    [InlineData("malformed-elf", Ps2DvdIdentificationStatus.Malformed)]
    [InlineData("no-boot2", Ps2DvdIdentificationStatus.NotIdentified)]
    public async Task InspectAsync_DecodedBootWithoutFacts_PreservesIntegrityMeasurement(string mutation, Ps2DvdIdentificationStatus expected)
    {
        byte[] logical = SyntheticPs2Iso.Create();
        if (mutation == "malformed-elf")
            BinaryPrimitives.WriteUInt16LittleEndian(logical.AsSpan(SyntheticPs2Iso.BootExecutableOffset + 40), 51);
        else
            SyntheticPs2Iso.SetConfiguration(logical, "VER = 1.00\r\nVMODE = NTSC\r\n");
        byte[] artifact = WithMetadata(StoredHunkFixture(logical).Artifact, DvdMarker());
        var candidate = ChdNativeCandidateTests.RepositoryNative();

        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(Write(artifact), candidate, 16L << 20, 16L << 20);

        AssertMeasurement(outcome, artifact, logical, candidate);
        outcome.ChdInspection!.DvdMetadata.ShouldBe(ExpectedDvdMetadata());
        outcome.Ps2Inspection!.Profile.ShouldBe("ps2-iso-root-elf/v1");
        outcome.Ps2Inspection.Status.ShouldBe(expected);
        outcome.Ps2Inspection.Facts.ShouldBeNull();
    }

    private async Task AssertMetadataRejectedAsync(byte[] artifact)
    {
        string path = Write(artifact);
        var candidate = ChdNativeCandidateTests.RepositoryNative();
        ChdInspectionOutcome outcome = await ChdInspectionService.InspectAsync(path, candidate, 16L << 20, 16L << 20);

        outcome.Status.ShouldBe(MediaWorkerStatus.Malformed);
        outcome.Rejection.ShouldBe(new ChdRejection(ChdRejectionStage.Decode, "InvalidDvdMetadata", path));
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.Inspection.ShouldBeNull();
        outcome.ChdInspection.ShouldBeNull();
        outcome.Ps2Inspection.ShouldBeNull();
    }

    private static void AssertMeasurement(ChdInspectionOutcome outcome, byte[] artifact, byte[] logical, ChdNativeCandidate candidate)
    {
        outcome.Status.ShouldBe(MediaWorkerStatus.Inspected);
        outcome.Rejection.ShouldBeNull();
        MediaInspection measured = outcome.Inspection.ShouldNotBeNull();
        measured.ArtifactBytes.ShouldBe(artifact.LongLength);
        measured.ArtifactSha256.ShouldBe(Digest(artifact));
        measured.LogicalBytes.ShouldBe(logical.LongLength);
        measured.LogicalSha256.ShouldBe(Digest(logical));
        measured.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(logical)));
        measured.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(logical)));
        measured.Crc32.ShouldBe(Crc(logical));
        measured.EncodingProfile.ShouldBe("chd-import/v1");
        measured.DiscKind.ShouldBeNull();
        outcome.NativeIdentity.ShouldBe(candidate.ExpectedIdentity);
        outcome.ChdInspection!.Version.ShouldBe(5U);
        outcome.ChdInspection.StoredRawSha1.ShouldBe(measured.Sha1);
        outcome.ChdInspection.ComputedRawSha1.ShouldBe(measured.Sha1);
        outcome.ChdInspection.StoredOverallSha1.ShouldBe(outcome.ChdInspection.ComputedOverallSha1);
        var reply = new MediaWorkerReply(outcome.Status,
            new(41, 41, 512L << 20, "decoder/exact", "worker/exact", null, 512L << 20, 0),
            measured, NativeIdentity: outcome.NativeIdentity, ChdInspection: outcome.ChdInspection,
            Ps2Inspection: outcome.Ps2Inspection);
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(reply), out var consumed).ShouldBeTrue();
        consumed.ShouldBe(reply);
    }

    private static ChdDvdMetadataObservation ExpectedDvdMetadata() => new("chd-dvd-metadata/v1", 1,
        0x44564420, 1, 1, 0, "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406");
    private static MetadataEntry DvdMarker() => new(0x44564420, 1, [0]);
    private sealed record MetadataEntry(uint Tag, byte Flags, byte[] Payload);

    // Append a real linked metadata chain; independently hash sorted tag/payload-hash records using checksum flag bit 0.
    private static byte[] WithMetadata(byte[] artifact, params MetadataEntry[] entries)
    {
        byte[] file = new byte[artifact.Length + entries.Sum(entry => 16 + entry.Payload.Length)];
        artifact.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(48), entries.Length == 0 ? 0 : (ulong)artifact.Length);
        int offset = artifact.Length;
        var records = new List<byte[]>();
        for (int index = 0; index < entries.Length; index++)
        {
            MetadataEntry entry = entries[index];
            Span<byte> descriptor = file.AsSpan(offset, 16);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor, entry.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor[4..], (uint)entry.Flags << 24 | (uint)entry.Payload.Length);
            BinaryPrimitives.WriteUInt64BigEndian(descriptor[8..], index + 1 == entries.Length ? 0 : (ulong)(offset + 16 + entry.Payload.Length));
            entry.Payload.CopyTo(file, offset + 16);
            offset += 16 + entry.Payload.Length;
            if ((entry.Flags & 1) == 0)
                continue;
            byte[] record = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(record, entry.Tag);
            SHA1.HashData(entry.Payload).CopyTo(record, 4);
            records.Add(record);
        }
        records.Sort((left, right) => left.AsSpan().SequenceCompareTo(right));
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        overall.AppendData(file, 64, 20);
        foreach (byte[] record in records)
            overall.AppendData(record);
        overall.GetHashAndReset().CopyTo(file, 84);
        return file;
    }

    private static long Overlap(long offset, int bytes, long start, long length) =>
        Math.Max(0, Math.Min(offset + bytes, start + length) - Math.Max(offset, start));

    // Serves one region's variant until that region has been read through once, then the original bytes.
    private sealed class FirstReadVariantSource(IChdDataSource inner, long start, byte[] variant) : IChdDataSource
    {
        private bool _served;
        public long Length => inner.Length;
        public int Read(long offset, Span<byte> destination)
        {
            int read = inner.Read(offset, destination);
            long from = Math.Max(offset, start), to = Math.Min(offset + read, start + variant.Length);
            if (_served || from >= to)
                return read;
            variant.AsSpan((int)(from - start), (int)(to - from)).CopyTo(destination[(int)(from - offset)..]);
            _served = to == start + variant.Length;
            return read;
        }
        public void Dispose() => inner.Dispose();
    }

    // Flips one byte near the end of a hunk and solves its final two bytes so the hunk's CRC16 is unchanged.
    private static byte[] SameCrc16Variant(byte[] hunk)
    {
        byte[] variant = hunk.ToArray();
        variant[^3] ^= 0x5A;
        ushort target = Crc16(hunk);
        ushort prefix = Crc16(variant.AsSpan(0, variant.Length - 2));
        for (int candidate = 0; candidate <= ushort.MaxValue; candidate++)
        {
            uint crc = prefix;
            foreach (byte value in new[] { (byte)(candidate >> 8), (byte)candidate })
            {
                crc ^= (uint)value << 8;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                crc &= 0xFFFF;
            }
            if (crc != target)
                continue;
            variant[^2] = (byte)(candidate >> 8);
            variant[^1] = (byte)candidate;
            variant.AsSpan().SequenceEqual(hunk).ShouldBeFalse();
            return variant;
        }
        throw new InvalidOperationException("No same-CRC16 variant exists.");
    }

    // One ChdFile never reads its source concurrently, so a plain list records every native artifact read in order.
    private sealed class RecordingSource(IChdDataSource inner) : IChdDataSource
    {
        public List<(long Offset, int Bytes)> Reads { get; } = [];
        public long Length => inner.Length;
        public int Read(long offset, Span<byte> destination)
        {
            int read = inner.Read(offset, destination);
            Reads.Add((offset, read));
            return read;
        }
        public void Dispose() => inner.Dispose();
    }

    private ChdNativeCandidate MissingCandidate() => new(Path.Combine(_directory, "absent-build"), ChdNativeCandidateTests.PrototypeIdentity());
    private string Write(byte[] bytes) { string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".chd"); File.WriteAllBytes(path, bytes); return path; }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Crc(byte[] bytes) { var crc = new System.IO.Hashing.Crc32(); crc.Append(bytes); return crc.GetCurrentHashAsUInt32().ToString("x8", System.Globalization.CultureInfo.InvariantCulture); }

    private static byte[] Header()
    {
        byte[] header = new byte[124];
        "MComprHD"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), 8192);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), 2048);
        return header;
    }

    // Three real CRC16-bearing stored hunks in a compressed v5 map; no native fixture generation.
    private static (byte[] Artifact, byte[] Logical, int Payload) Fixture()
    {
        const int hunkBytes = 4096, hunks = 3, logicalBytes = 10240;
        byte[][] payloads = Enumerable.Range(0, hunks).Select(index => Enumerable.Range(0, hunkBytes).Select(at => (byte)((index * 73 + at * 29 + 7) % 251)).ToArray()).ToArray();
        byte[] logical = payloads.SelectMany(bytes => bytes).Take(logicalBytes).ToArray();
        var (artifact, start) = StoredHunkFixture(logical, payloads);
        return (artifact, logical, start);
    }

    private static (byte[] Artifact, int Payload) StoredHunkFixture(byte[] logical, byte[][]? payloads = null, int hunkBytes = 4096)
    {
        int hunks = (logical.Length + hunkBytes - 1) / hunkBytes;
        if (payloads is null)
        {
            payloads = Enumerable.Range(0, hunks).Select(_ => new byte[hunkBytes]).ToArray();
            for (int index = 0; index < hunks; index++)
                logical.AsSpan(index * hunkBytes, Math.Min(hunkBytes, logical.Length - index * hunkBytes)).CopyTo(payloads[index]);
        }
        var bits = new List<bool>();
        void Bits(uint value, int count)
        { for (int bit = count - 1; bit >= 0; bit--) bits.Add((value & (1U << bit)) != 0); }
        for (int code = 0; code < 16; code++)
            Bits(4, 4);
        for (int index = 0; index < hunks; index++)
            Bits(4, 4);
        foreach (byte[] payload in payloads)
            Bits(Crc16(payload), 16);
        byte[] packed = new byte[(bits.Count + 7) / 8];
        for (int bit = 0; bit < bits.Count; bit++)
            if (bits[bit])
                packed[bit / 8] |= (byte)(1 << (7 - bit % 8));
        int start = 140 + packed.Length;
        byte[] file = new byte[start + hunks * hunkBytes];
        Header().CopyTo(file, 0);
        "zlib"u8.CopyTo(file.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(32), (ulong)logical.LongLength);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(40), 124);
        byte[] rawMap = new byte[hunks * 12];
        for (int index = 0; index < hunks; index++)
        {
            Span<byte> entry = rawMap.AsSpan(index * 12, 12);
            entry[0] = 4;
            WriteNumber(entry.Slice(1, 3), checked((ulong)hunkBytes));
            WriteNumber(entry.Slice(4, 6), (ulong)(start + index * hunkBytes));
            BinaryPrimitives.WriteUInt16BigEndian(entry[10..], Crc16(payloads[index]));
            payloads[index].CopyTo(file, start + index * hunkBytes);
        }
        Span<byte> map = file.AsSpan(124, 16);
        BinaryPrimitives.WriteUInt32BigEndian(map, (uint)packed.Length);
        WriteNumber(map.Slice(4, 6), (ulong)start);
        BinaryPrimitives.WriteUInt16BigEndian(map[10..], Crc16(rawMap));
        map[12] = 16;
        map[13] = 24;
        map[14] = 8;
        packed.CopyTo(file, 140);
        byte[] raw = SHA1.HashData(logical);
        raw.CopyTo(file, 64);
        SHA1.HashData(raw).CopyTo(file, 84);
        return (file, start);
    }

    private static void WriteNumber(Span<byte> destination, ulong value)
    { for (int index = destination.Length - 1; index >= 0; index--) { destination[index] = (byte)value; value >>= 8; } }
    private static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            crc &= 0xFFFF;
        }
        return (ushort)crc;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
