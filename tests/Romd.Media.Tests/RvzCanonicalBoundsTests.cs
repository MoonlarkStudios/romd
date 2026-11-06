using System.Buffers.Binary;
using Romd.Media.Rvz;
using Romd.Media.Nintendo;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzCanonicalBoundsTests
{
    private const long MaximumLogicalBytes = 16L << 30;

    [Theory]
    [InlineData(129, 17)]
    [InlineData(131071, 23)]
    [InlineData(131072, 31)]
    [InlineData(131073, 42)]
    [InlineData(393219, 53)]
    public async Task EncodeGameCubeAsync_RandomCanonicalInput_FitsPerInputReservationAndDecodesEveryByte(int length, int seed)
    {
        byte[] original = GameCubeIsoFixture.Create(length, seed);
        var bound = RvzCanonicalBounds.GetGameCubeOutputBound(length, length)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        await using var source = new MemoryContent(original);
        using var output = new BudgetedOutput(bound.MaximumOutputBytes);

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            RvzCanonicalBounds.GameCubeOptions(length, bound.MaximumOutputBytes), CancellationToken.None);

        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.ArtifactBytes.ShouldBe(output.Length);
        success.ArtifactBytes.ShouldBeGreaterThan(length);
        success.ArtifactBytes.ShouldBeLessThanOrEqualTo(bound.MaximumOutputBytes);
        success.EncoderBuild.ShouldBe(RvzEncoder.EncoderBuild);
        output.MaximumWrittenEnd.ShouldBe(output.Length);
        source.Disposed.ShouldBeFalse();
        output.CanWrite.ShouldBeTrue();
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        RvzCanonicalRecipe.Matches(decoded.Descriptor.Compression, decoded.Descriptor.CompressionLevel,
            decoded.Descriptor.ChunkSize).ShouldBeTrue();
        byte[] restored = new byte[length];
        (await decoded.ReadAtAsync(0, restored, CancellationToken.None)).ShouldBe(length);
        restored.ShouldBe(original);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_IncompressibleMinimalInput_AccountsForCompressedTableExpansion()
    {
        byte[] original = GameCubeIsoFixture.Create(129);
        var bound = RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        using var output = new BudgetedOutput(bound.MaximumOutputBytes);

        (await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output,
            RvzCanonicalBounds.GameCubeOptions(MaximumLogicalBytes, bound.MaximumOutputBytes), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();

        byte[] artifact = output.ToArray();
        uint rawStored = BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 192));
        uint groupsStored = BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 208));
        checked(rawStored + groupsStored).ShouldBeGreaterThan(24u + 12u);
        int rawOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 184)));
        byte[] actualRawTable = new byte[24];
        using var decompressor = new ZstdSharp.Decompressor();
        decompressor.Unwrap(artifact.AsSpan(rawOffset, checked((int)rawStored)), actualRawTable).ShouldBe(24);
        byte[] expectedRawTable = new byte[24];
        BinaryPrimitives.WriteUInt64BigEndian(expectedRawTable, 0x80);
        BinaryPrimitives.WriteUInt64BigEndian(expectedRawTable.AsSpan(8), (ulong)(original.Length - 0x80));
        BinaryPrimitives.WriteUInt32BigEndian(expectedRawTable.AsSpan(20), 1);
        actualRawTable.ShouldBe(expectedRawTable);
        output.Length.ShouldBeGreaterThan(0x124L + original.Length + 24 + 12);
        output.Length.ShouldBeLessThanOrEqualTo(bound.MaximumOutputBytes);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(artifact), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        byte[] restored = new byte[original.Length];
        (await decoded.ReadAtAsync(0, restored, CancellationToken.None)).ShouldBe(original.Length);
        restored.ShouldBe(original);
    }

    [Theory]
    [InlineData(10L << 30)]
    [InlineData(16L << 30)]
    public void GetGameCubeOutputBound_LargeDeclaredLength_ReservesOnlyBoundedContainerOverhead(long length)
    {
        var bound = RvzCanonicalBounds.GetGameCubeOutputBound(length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();

        bound.MaximumOutputBytes.ShouldBeGreaterThan(length);
        bound.MaximumOutputBytes.ShouldBeLessThanOrEqualTo(length + (2L << 20));
    }

    [Fact]
    public async Task EncodeGameCubeAsync_CanonicalOutputCapBelowActualArtifact_RejectsAndClearsPartialOutput()
    {
        byte[] original = GameCubeIsoFixture.Create(129);
        var bound = RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        using var measured = new MemoryStream();
        var completed = (await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), measured,
            RvzCanonicalBounds.GameCubeOptions(MaximumLogicalBytes, bound.MaximumOutputBytes), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();
        long cap = completed.ArtifactBytes - 1;
        using var limited = new BudgetedOutput(cap);

        var failure = await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), limited,
            RvzCanonicalBounds.GameCubeOptions(MaximumLogicalBytes, cap), CancellationToken.None);

        failure.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.OutputLimitExceeded);
        limited.MaximumWrittenEnd.ShouldBeGreaterThan(0);
        limited.MaximumWrittenEnd.ShouldBeLessThanOrEqualTo(cap);
        limited.Length.ShouldBe(0);
        limited.Position.ShouldBe(0);
        limited.CanWrite.ShouldBeTrue();
    }

    [Theory]
    [InlineData(long.MinValue, MaximumLogicalBytes, RvzEncodeFailureKind.InvalidSource)]
    [InlineData(0L, MaximumLogicalBytes, RvzEncodeFailureKind.InvalidSource)]
    [InlineData(128L, MaximumLogicalBytes, RvzEncodeFailureKind.InvalidSource)]
    [InlineData(129L, -1L, RvzEncodeFailureKind.InvalidOptions)]
    [InlineData(129L, 128L, RvzEncodeFailureKind.InvalidOptions)]
    [InlineData(129L, long.MaxValue, RvzEncodeFailureKind.InvalidOptions)]
    [InlineData(129L, MaximumLogicalBytes + 1, RvzEncodeFailureKind.InvalidOptions)]
    [InlineData(130L, 129L, RvzEncodeFailureKind.LogicalLimitExceeded)]
    [InlineData(MaximumLogicalBytes + 1, MaximumLogicalBytes, RvzEncodeFailureKind.LogicalLimitExceeded)]
    [InlineData(long.MaxValue, MaximumLogicalBytes, RvzEncodeFailureKind.LogicalLimitExceeded)]
    public void GetGameCubeOutputBound_TruncatedOversizedOrHostileLimits_ReturnsTypedFailureBeforeOverflow(
        long length, long limit, RvzEncodeFailureKind expected)
    {
        var failure = RvzCanonicalBounds.GetGameCubeOutputBound(length, limit)
            .ShouldBeOfType<RvzCanonicalOutputBound.Failure>();

        failure.Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(MaximumLogicalBytes + 1)]
    [InlineData(long.MaxValue)]
    public async Task EncodeGameCubeAsync_OversizedDeclaredLength_RejectsWithoutReadingOrCreatingOutput(long length)
    {
        await using var source = new DeclaredLengthContent(length);
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            RvzCanonicalBounds.GameCubeOptions(MaximumLogicalBytes, 0x124), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.LogicalLimitExceeded);
        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Fact]
    public void GetGameCubeOutputBound_PreCancelledToken_PropagatesTokenBeforeReturningFacts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = Should.Throw<OperationCanceledException>(() =>
            RvzCanonicalBounds.GetGameCubeOutputBound(long.MaxValue, long.MaxValue, cancellation.Token));

        failure.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_CanonicalReservationCancelledDuringRead_ClearsPartialOutput()
    {
        byte[] original = GameCubeIsoFixture.Create(131073);
        var bound = RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        await using var source = new MemoryContent(original)
        {
            BeforeRead = _ =>
            {
                if (++reads == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        using var output = new BudgetedOutput(bound.MaximumOutputBytes);

        var failure = await Should.ThrowAsync<OperationCanceledException>(() =>
            new RvzEncoder().EncodeGameCubeAsync(source, output,
                RvzCanonicalBounds.GameCubeOptions(MaximumLogicalBytes, bound.MaximumOutputBytes), cancellation.Token));

        failure.CancellationToken.ShouldBe(cancellation.Token);
        output.MaximumWrittenEnd.ShouldBeGreaterThan(0);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
        output.CanWrite.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task EncodeAsync_WiiMaximumExceptionsAndSplitPartitions_FitsCanonicalReservation(int partitions, bool oneDirectoryGroup)
    {
        byte[] standard = new byte[16], korean = new byte[16];
        new Random(419).NextBytes(standard);
        new Random(431).NextBytes(korean);
        var fixture = SyntheticWiiIsoBuilder.Create(korean, 1, sectors: 5, partitions: partitions,
            poisonHashes: true, oneDirectoryGroup: oneDirectoryGroup);
        var bound = RvzCanonicalBounds.GetWiiOutputBound(fixture.Iso.Length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        using var output = new BudgetedOutput(bound.MaximumOutputBytes);
        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            RvzCanonicalBounds.WiiOptions(MaximumLogicalBytes, bound.MaximumOutputBytes), CancellationToken.None);
        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.ArtifactBytes.ShouldBeLessThanOrEqualTo(bound.MaximumOutputBytes);
        output.MaximumWrittenEnd.ShouldBe(output.Length);
        byte[] artifact = output.ToArray();
        int partitionAt = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 152)));
        int tableAt = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 200)));
        int tableStored = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 208)));
        int groups = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 196)));
        byte[] table = new byte[groups * 12];
        using var decompressor = new ZstdSharp.Decompressor();
        decompressor.Unwrap(artifact.AsSpan(tableAt, tableStored), table).ShouldBe(table.Length);
        for (int partition = 0; partition < partitions; partition++)
        {
            int firstGroup = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(partitionAt + partition * 48 + 24)));
            int at = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(firstGroup * 12)) * 4L));
            uint sizeAndFlag = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(firstGroup * 12 + 4));
            int stored = (int)(sizeAndFlag & 0x7fffffffu);
            byte[] data = new byte[4 * 0x7c00 + 4578 + 3];
            int decoded;
            if ((sizeAndFlag & 0x80000000u) != 0)
                decoded = decompressor.Unwrap(artifact.AsSpan(at, stored), data);
            else
            { artifact.AsSpan(at, stored).CopyTo(data); decoded = stored; }
            BinaryPrimitives.ReadUInt16BigEndian(data).ShouldBe((ushort)208);
            (2 + 208 * 22).ShouldBe(4578);
            decoded.ShouldBeLessThanOrEqualTo(4 * 0x7c00 + 4578 + 3);
        }
        await using var image = await RvzImage.OpenAsync(new MemoryContent(artifact), new(), new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        image.Descriptor.DiscType.ShouldBe(2u);
        byte[] restored = new byte[fixture.Iso.Length];
        (await image.ReadAtAsync(0, restored, CancellationToken.None)).ShouldBe(restored.Length);
        restored.AsSpan().SequenceEqual(fixture.Iso).ShouldBeTrue("bounded Wii output must preserve every encrypted sector and exception without key diagnostics");
    }

    [Fact]
    public async Task EncodeAsync_Wii64IndependentPartitionExtents_FitsReservedBoundAndDecodesCompleteIso()
    {
        var single = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(1), sectors: 5, poisonHashes: true);
        var template = single.Partitions[0];
        const int partitionCount = 64;
        int first = checked((int)template.Offset);
        int partitionBytes = checked((int)(template.DataOffset - template.Offset) + template.Sectors * 32768);
        int tailStart = first + partitionBytes;
        int tailBytes = single.Iso.Length - tailStart;
        byte[] original = new byte[checked(first + partitionCount * partitionBytes + tailBytes)];
        single.Iso.AsSpan(0, first).CopyTo(original);
        // Rebuild the complete directory independently. Partition metadata uses
        // relative offsets and sector IVs do not depend on the disc-global offset.
        original.AsSpan(0x40000, 0x20 + partitionCount * 8).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(original.AsSpan(0x40000), partitionCount);
        BinaryPrimitives.WriteUInt32BigEndian(original.AsSpan(0x40004), 0x40020 / 4);
        for (int index = 0; index < partitionCount; index++)
        {
            int offset = checked(first + index * partitionBytes);
            single.Iso.AsSpan(first, partitionBytes).CopyTo(original.AsSpan(offset));
            BinaryPrimitives.WriteUInt32BigEndian(original.AsSpan(0x40020 + index * 8), checked((uint)offset / 4));
            BinaryPrimitives.WriteUInt32BigEndian(original.AsSpan(0x40024 + index * 8), 0);
        }
        single.Iso.AsSpan(tailStart, tailBytes).CopyTo(original.AsSpan(first + partitionCount * partitionBytes));
        var layout = await NintendoIsoInspector.InspectAsync(new MemoryContent(original), MaximumLogicalBytes, CancellationToken.None);
        layout.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
        layout.Layout!.WiiPartitionCount.ShouldBe(partitionCount);
        var bound = RvzCanonicalBounds.GetWiiOutputBound(original.Length, MaximumLogicalBytes)
            .ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        using var output = new BudgetedOutput(bound.MaximumOutputBytes);
        var result = await new RvzWiiEncoder().EncodeAsync(new MemoryContent(original), output,
            RvzCanonicalBounds.WiiOptions(MaximumLogicalBytes, bound.MaximumOutputBytes), CancellationToken.None);
        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.ArtifactBytes.ShouldBeLessThanOrEqualTo(bound.MaximumOutputBytes);
        output.MaximumWrittenEnd.ShouldBe(output.Length);
        await using var image = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new(), new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        image.Descriptor.DiscType.ShouldBe(2u);
        image.Descriptor.HasWiiPartitions.ShouldBeTrue();
        byte[] restored = new byte[original.Length];
        (await image.ReadAtAsync(0, restored, CancellationToken.None)).ShouldBe(restored.Length);
        restored.AsSpan().SequenceEqual(original).ShouldBeTrue("all 64 encrypted partitions and raw gaps must survive the bounded container round trip");
    }

    [Theory]
    [InlineData(129L)]
    [InlineData(32768L)]
    [InlineData(131073L)]
    [InlineData(10L << 30)]
    [InlineData(16L << 30)]
    public void GetWiiOutputBound_AccountsForAllWorstCasePartitionAndTableCosts(long length)
    {
        long sectors = length / 32768;
        long partitionGroups = (sectors + 3) / 4 + 63;
        long groups = (length + 131071) / 131072 + 128;
        long expected = 292 + length + 120 * sectors + 5 * partitionGroups + 3 * groups + 3072 +
            ZstdSharp.Compressor.GetCompressBound(1560) + ZstdSharp.Compressor.GetCompressBound((int)(12 * groups));
        var bound = RvzCanonicalBounds.GetWiiOutputBound(length, MaximumLogicalBytes).ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        bound.MaximumOutputBytes.ShouldBe(expected);
        bound.MaximumOutputBytes.ShouldBeGreaterThan(((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(length, MaximumLogicalBytes)).MaximumOutputBytes);
        var options = RvzCanonicalBounds.WiiOptions(MaximumLogicalBytes, bound.MaximumOutputBytes);
        options.Compression.ShouldBe(RvzEncodeCompression.Zstandard);
        options.CompressionLevel.ShouldBe(19);
        options.ChunkBytes.ShouldBe(131072);
        options.PackJunk.ShouldBeTrue();
    }

    [Theory]
    [InlineData(long.MinValue, MaximumLogicalBytes, RvzEncodeFailureKind.InvalidSource)]
    [InlineData(128L, MaximumLogicalBytes, RvzEncodeFailureKind.InvalidSource)]
    [InlineData(129L, long.MaxValue, RvzEncodeFailureKind.InvalidOptions)]
    [InlineData(long.MaxValue, MaximumLogicalBytes, RvzEncodeFailureKind.LogicalLimitExceeded)]
    public void GetWiiOutputBound_HostileLengthOrLimit_ReturnsFailureBeforeArithmetic(long length, long limit, RvzEncodeFailureKind kind) =>
        RvzCanonicalBounds.GetWiiOutputBound(length, limit).ShouldBeOfType<RvzCanonicalOutputBound.Failure>().Kind.ShouldBe(kind);

    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(3L, 131071L)]
    [InlineData(5L, 131073L)]
    public void GetWiiOutputBound_Maximum64PartitionAnd65RawAreaSplits_CoversEveryRoundedGroup(long sectorsPerPartition, long bytesPerRawArea)
    {
        const long partitions = 64, rawAreas = 65;
        long sectors = partitions * sectorsPerPartition;
        long length = sectors * 32768 + rawAreas * bytesPerRawArea;
        long actualPartitionGroups = partitions * ((sectorsPerPartition + 3) / 4);
        long actualRawGroups = rawAreas * ((bytesPerRawArea + 131071) / 131072);
        long actualGroups = actualPartitionGroups + actualRawGroups;
        // Summing ceilings across P parts costs at most P-1 extra groups.
        actualPartitionGroups.ShouldBeLessThanOrEqualTo((sectors + 3) / 4 + partitions - 1);
        actualGroups.ShouldBeLessThanOrEqualTo((length + 131071) / 131072 + partitions + rawAreas - 1);
        long actualWorst = 292 + length + 120 * sectors + 5 * actualPartitionGroups + 3 * actualGroups + 3072 +
            ZstdSharp.Compressor.GetCompressBound(1560) + ZstdSharp.Compressor.GetCompressBound((int)(12 * actualGroups));
        var bound = RvzCanonicalBounds.GetWiiOutputBound(length, MaximumLogicalBytes).ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        bound.MaximumOutputBytes.ShouldBeGreaterThanOrEqualTo(actualWorst);
    }

    [Fact]
    public void GetWiiOutputBound_CancelledToken_PreservesCancellationIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => RvzCanonicalBounds.GetWiiOutputBound(long.MaxValue, long.MaxValue, cancellation.Token))
            .CancellationToken.ShouldBe(cancellation.Token);
    }

    private sealed class BudgetedOutput(long budget) : MemoryStream
    {
        public long MaximumWrittenEnd { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            long end = checked(Position + buffer.Length);
            if (end > budget)
                throw new InvalidOperationException("Encoder write exceeds its per-input output reservation.");
            MaximumWrittenEnd = Math.Max(MaximumWrittenEnd, end);
            return base.WriteAsync(buffer, ct);
        }
    }

    private sealed class DeclaredLengthContent(long length) : IRandomAccessContent
    {
        public int Reads { get; private set; }
        public long Length => length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            Reads++;
            throw new InvalidOperationException("Oversized source must be rejected before reading.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
