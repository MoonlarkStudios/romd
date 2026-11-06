using Romd.Media.Nintendo;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;
using static Romd.Media.TestSupport.SyntheticNintendoIso;

namespace Romd.Media.Tests;

public sealed class NintendoIsoInspectionTests
{
    [Fact]
    public async Task InspectAsync_GameCubeLayout_ReturnsKeyFreeStructuralFactsWithoutReadingFileData()
    {
        var source = new ObservedContent(GameCube());

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
        result.Layout.ShouldBe(new NintendoIsoLayout(NintendoDiscKind.GameCube, source.Length, 0, 0, 0));
        source.MaximumReadEnd.ShouldBeLessThan(GameCubeFileOffset);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 1)]
    public async Task InspectAsync_StandardOrKoreanTicketIndex_ReportsSelectedProfile(byte keyIndex, int standard, int korean)
    {
        var source = new ObservedContent(Wii(keyIndex));

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
        result.Layout.ShouldBe(new NintendoIsoLayout(NintendoDiscKind.Wii, source.Length, 1, standard, korean));
        source.MaximumReadEnd.ShouldBe(WiiPartitionOffset + 0x2C0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_MixedKeyPartitionsAcrossDirectoryGroups_ReportsAllPartitions()
    {
        byte[] bytes = Wii(0, 1);
        U32(bytes, WiiDirectoryOffset, 1);
        U32(bytes, WiiDirectoryOffset + 8, 1);
        U32(bytes, WiiDirectoryOffset + 12, (WiiTableOffset + 8) / 4);

        var result = await InspectAsync(bytes);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
        result.Layout.ShouldBe(new NintendoIsoLayout(NintendoDiscKind.Wii, bytes.Length, 2, 1, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_ShortPositionalReads_CompletesLayoutInspection(bool wii)
    {
        var source = new ObservedContent(wii ? Wii(1) : GameCube()) { MaximumReadBytes = 7 };

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
        source.Reads.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task InspectAsync_GameCubeAliasedFileData_AcceptsIndividuallyValidExtents()
    {
        byte[] bytes = GameCube();
        U32(bytes, 0x428, 48);
        U32(bytes, GameCubeFstOffset + 8, 3);
        U32(bytes, GameCubeFstOffset + 24, 1);
        U32(bytes, GameCubeFstOffset + 28, GameCubeFileOffset);
        U32(bytes, GameCubeFstOffset + 32, 0x1000);
        bytes.AsSpan(GameCubeFstOffset + 36, 12).Clear();
        "a.bin\0"u8.CopyTo(bytes.AsSpan(GameCubeFstOffset + 37));

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
    }

    [Fact]
    public async Task InspectAsync_GameCubeFilenameAtStringOffsetZero_AcceptsValidFileName()
    {
        byte[] bytes = GameCube();
        U32(bytes, GameCubeFstOffset + 12, 0);
        "a.bin\0"u8.CopyTo(bytes.AsSpan(GameCubeFstOffset + 24));

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
    }

    [Fact]
    public async Task InspectAsync_GameCubeEmptyFilesystem_DoesNotRequireUnusedNameBytes()
    {
        byte[] bytes = GameCube();
        U32(bytes, 0x428, 12);
        U32(bytes, GameCubeFstOffset + 8, 1);

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
    }

    [Fact]
    public async Task InspectAsync_GameCubeNestedDirectory_AcceptsBoundedParentAndSubtree()
    {
        byte[] bytes = GameCube();
        U32(bytes, 0x428, 48);
        U32(bytes, GameCubeFstOffset + 8, 3);
        bytes[GameCubeFstOffset + 12] = 1;
        U32(bytes, GameCubeFstOffset + 16, 0);
        U32(bytes, GameCubeFstOffset + 20, 3);
        U32(bytes, GameCubeFstOffset + 24, 1);
        U32(bytes, GameCubeFstOffset + 28, GameCubeFileOffset);
        U32(bytes, GameCubeFstOffset + 32, 0x1000);
        bytes.AsSpan(GameCubeFstOffset + 36, 12).Clear();
        "a.bin\0"u8.CopyTo(bytes.AsSpan(GameCubeFstOffset + 37));

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
    }

    [Fact]
    public async Task InspectAsync_HostileGameCubeEntryCount_IsRejectedBeforeReadingTheFst()
    {
        byte[] bytes = GameCube();
        Array.Resize(ref bytes, 2 << 20);
        U32(bytes, 0x428, 100_001 * 12 + 1);
        U32(bytes, GameCubeFstOffset + 8, 100_001);
        var source = new ObservedContent(bytes);

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        source.RequestedBytes.ShouldBe(0x80 + 0x440 + 0x20 + 0x100 + 12);
    }

    [Fact]
    public async Task InspectAsync_HostileGameCubeFstByteCount_IsRejectedBeforeAllocatingTheFst()
    {
        byte[] bytes = GameCube();
        U32(bytes, 0x428, (16 << 20) + 1);
        var source = new ObservedContent(bytes) { DeclaredLength = 32L << 20 };

        var result = await NintendoIsoInspector.InspectAsync(source, 32L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        source.RequestedBytes.ShouldBe(0x80 + 0x440 + 0x20);
    }

    [Fact]
    public async Task InspectAsync_SixtyFourPartitions_ReadsOnlyBoundedHeaders()
    {
        var indices = Enumerable.Range(0, 64).Select(index => (byte)(index % 2)).ToArray();
        var source = new ObservedContent(Wii(indices));

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Layout.ShouldBe(new NintendoIsoLayout(NintendoDiscKind.Wii, source.Length, 64, 32, 32));
        source.RequestedBytes.ShouldBe(0x80 + 32 + 64 * (8 + 0x2C0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task InspectAsync_InvalidGameCubeMetadataOrFileExtent_RejectsMalformed(int mutation)
    {
        byte[] bytes = GameCube();
        switch (mutation)
        {
            case 0:
                U32(bytes, 0x2440 + 0x14, uint.MaxValue);
                break;
            case 1:
                U32(bytes, 0x420, 0x2440);
                break;
            case 2:
                U32(bytes, 0x424, GameCubeDolOffset);
                break;
            case 3:
                U32(bytes, GameCubeDolOffset + 0x90, uint.MaxValue);
                break;
            case 4:
                U32(bytes, GameCubeFstOffset + 8, uint.MaxValue);
                break;
            case 5:
                U32(bytes, GameCubeFstOffset + 12, 0xFFFFFF);
                break;
            case 6:
                U32(bytes, GameCubeFstOffset + 20, 0x1001);
                break;
            case 7:
                bytes[GameCubeFstOffset + 12] = 2;
                break;
            case 8:
                bytes.AsSpan(GameCubeFstOffset + 25, 7).Fill((byte)'x');
                break;
        }

        var result = await InspectAsync(bytes);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
        result.Layout.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InspectAsync_InvalidGameCubeDirectoryNesting_RejectsMalformed(int mutation)
    {
        byte[] bytes = GameCube();
        bytes[GameCubeFstOffset + 12] = 1;
        U32(bytes, GameCubeFstOffset + 16, mutation == 0 ? 1U : 0U);
        U32(bytes, GameCubeFstOffset + 20, mutation switch { 1 => 1U, 2 => 3U, _ => 2U });

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task InspectAsync_InvalidWiiTablesOrPartitionExtents_RejectsMalformed(int mutation)
    {
        byte[] bytes = Wii(0, 1);
        switch (mutation)
        {
            case 0:
                U32(bytes, WiiDirectoryOffset + 4, WiiDirectoryOffset / 4);
                break;
            case 1:
                U32(bytes, WiiDirectoryOffset + 4, uint.MaxValue);
                break;
            case 2:
                U32(bytes, WiiDirectoryOffset + 8, 1);
                U32(bytes, WiiDirectoryOffset + 12, WiiTableOffset / 4);
                break;
            case 3:
                U32(bytes, WiiTableOffset, WiiTableOffset / 4);
                break;
            case 4:
                U32(bytes, WiiTableOffset + 8, WiiPartitionOffset / 4);
                break;
            case 5:
                U32(bytes, WiiPartitionOffset + 0x2B8, 0x20004 / 4);
                break;
            case 6:
                U32(bytes, WiiPartitionOffset + 0x2BC, 0x8004 / 4);
                break;
            case 7:
                U32(bytes, WiiPartitionOffset + 0x2B4, 0x100 / 4);
                break;
            case 8:
                U32(bytes, WiiPartitionOffset + 0x2B0, 0x2C0 / 4);
                break;
            case 9:
                U32(bytes, WiiPartitionOffset + 0x2B8, uint.MaxValue);
                break;
        }

        var result = await InspectAsync(bytes);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
        result.Layout.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InspectAsync_UnsupportedWiiProfile_RejectsWithoutClaimingEligibility(int mutation)
    {
        byte[] bytes = Wii(0);
        switch (mutation)
        {
            case 0:
                bytes[0x60] = 1;
                break;
            case 1:
                bytes[0x61] = 1;
                break;
            case 2:
                U32(bytes, WiiPartitionOffset, 0x00010000);
                break;
            case 3:
                bytes[WiiPartitionOffset + 0x1F1] = 2;
                break;
            case 4:
                U32(bytes, WiiDirectoryOffset, 0);
                break;
        }

        var result = await InspectAsync(bytes);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        result.Layout.ShouldBeNull();
    }

    [Theory]
    [InlineData(0x60)]
    [InlineData(0x61)]
    public async Task InspectAsync_InvalidWiiFlagValue_RejectsMalformed(int offset)
    {
        byte[] bytes = Wii(0);
        bytes[offset] = 2;

        (await InspectAsync(bytes)).Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
    }

    [Fact]
    public async Task InspectAsync_HostilePartitionCount_IsRejectedBeforeTableAllocationOrReading()
    {
        byte[] bytes = Wii(0);
        U32(bytes, WiiDirectoryOffset, uint.MaxValue);
        var source = new ObservedContent(bytes);

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        source.Reads.ShouldBe(2);
        source.RequestedBytes.ShouldBe(0x80 + 32);
    }

    [Fact]
    public async Task InspectAsync_UnknownContent_DoesNotInferEligibility()
    {
        byte[] bytes = GameCube();
        U32(bytes, 0x1C, 0);

        var result = await InspectAsync(bytes);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
        result.Layout.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_SourceExceedsLogicalLimit_DoesNotReadSource()
    {
        var source = new ObservedContent(Wii(0));

        var result = await NintendoIsoInspector.InspectAsync(source, source.Length - 1, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.LimitExceeded);
        source.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_TruncatedDeclaredLayout_RejectsMalformed(bool wii)
    {
        byte[] bytes = wii ? Wii(0) : GameCube();

        (await InspectAsync(bytes[..^1])).Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectAsync_SourceReturnsUnexpectedEndOfFile_RejectsMalformed(bool wii)
    {
        var source = new ObservedContent(wii ? Wii(0) : GameCube()) { EofAfterRead = 2 };

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
    }

    [Fact]
    public async Task InspectAsync_SourceLengthChanges_RejectsFacts()
    {
        var source = new ObservedContent(GameCube()) { ChangeLengthAfterRead = 2 };

        var result = await NintendoIsoInspector.InspectAsync(source, 16L << 20, CancellationToken.None);

        result.Status.ShouldBe(NintendoIsoInspectionStatus.Malformed);
        result.Layout.ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_CanceledBetweenPartialReads_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ObservedContent(Wii(0)) { MaximumReadBytes = 7, AfterRead = () => cancellation.Cancel() };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            NintendoIsoInspector.InspectAsync(source, 16L << 20, cancellation.Token));

        source.Reads.ShouldBe(1);
        source.Disposed.ShouldBeFalse();
    }

    private static async Task<NintendoIsoInspectionResult> InspectAsync(byte[] bytes, CancellationToken ct = default) =>
        await NintendoIsoInspector.InspectAsync(new MemoryContent(bytes), 16L << 20, ct);

    private sealed class ObservedContent(byte[] bytes) : IRandomAccessContent
    {
        public int MaximumReadBytes { get; init; } = int.MaxValue;
        public int EofAfterRead { get; init; } = int.MaxValue;
        public int ChangeLengthAfterRead { get; init; } = int.MaxValue;
        public long? DeclaredLength { get; init; }
        public Action? AfterRead { get; init; }
        public int Reads { get; private set; }
        public long RequestedBytes { get; private set; }
        public long MaximumReadEnd { get; private set; }
        public bool Disposed { get; private set; }
        public long Length => (DeclaredLength ?? bytes.Length) + (Reads >= ChangeLengthAfterRead ? 1 : 0);
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            RequestedBytes += destination.Length;
            if (Reads > EofAfterRead)
                return ValueTask.FromResult(0);
            int count = (int)Math.Min(Math.Min(destination.Length, MaximumReadBytes), bytes.Length - offset);
            bytes.AsMemory((int)offset, count).CopyTo(destination);
            MaximumReadEnd = Math.Max(MaximumReadEnd, offset + count);
            AfterRead?.Invoke();
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
