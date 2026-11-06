using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzEncoderTests
{
    [Theory]
    [InlineData(RvzEncodeCompression.None, 0, 0x10000)]
    [InlineData(RvzEncodeCompression.Zstandard, 1, 0x8000)]
    [InlineData(RvzEncodeCompression.Zstandard, 19, 0x10000)]
    [InlineData(RvzEncodeCompression.Zstandard, 22, 0x20000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x80000)]
    [InlineData(RvzEncodeCompression.Zstandard, 15, 0x200000)]
    public async Task EncodeGameCubeAsync_ExplicitRecipes_ReconstructsEveryByteAndFullHashes(
        RvzEncodeCompression compression, int level, int chunk)
    {
        byte[] original = GameCubeIsoFixture.Create(chunk * 3 + 773);
        GameCubeIsoFixture.AddPadding(original, 123, Math.Min(chunk - 123, 0x18000));
        Array.Clear(original, chunk, chunk);
        var source = new MemoryContent(original);
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(compression, level, chunk), CancellationToken.None);

        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.LogicalBytes.ShouldBe(original.Length);
        success.ArtifactBytes.ShouldBe(output.Length);
        success.Groups.ShouldBe(4);
        success.ZeroGroups.ShouldBe(1);
        success.PackedGroups.ShouldBeGreaterThan(0);
        success.EncoderBuild.ShouldStartWith("romd-media-encoder/mvid:");
        source.Disposed.ShouldBeFalse();
        output.CanWrite.ShouldBeTrue();
        output.Position.ShouldBe(output.Length);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(2L << 20), ct: CancellationToken.None);
        decoded.Descriptor.DiscType.ShouldBe(1u);
        decoded.Descriptor.HasWiiPartitions.ShouldBeFalse();
        decoded.Descriptor.Compression.ShouldBe((uint)compression);
        decoded.Descriptor.CompressionLevel.ShouldBe(level);
        decoded.Descriptor.ChunkSize.ShouldBe(chunk);
        byte[] actual = await ReadAllAsync(decoded, CancellationToken.None);
        actual.ShouldBe(original);
        SHA256.HashData(actual).ShouldBe(SHA256.HashData(original));
        SHA1.HashData(actual).ShouldBe(SHA1.HashData(original));
        MD5.HashData(actual).ShouldBe(MD5.HashData(original));
        Crc32.Hash(actual).ShouldBe(Crc32.Hash(original));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EncodeGameCubeAsync_SameInputRecipeAndBuild_ProducesDeterministicArtifact(bool packJunk)
    {
        byte[] original = GameCubeIsoFixture.Create(0x70000 + 19);
        GameCubeIsoFixture.AddPadding(original, 0x12345, 0x24000);
        var options = new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 19, 0x10000) { PackJunk = packJunk };
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        var encoder = new RvzEncoder();

        var a = await encoder.EncodeGameCubeAsync(new MemoryContent(original), first, options, CancellationToken.None);
        var b = await encoder.EncodeGameCubeAsync(new MemoryContent(original), second, options, CancellationToken.None);

        a.ShouldBeOfType<RvzEncodeResult.Success>().ShouldBe(b.ShouldBeOfType<RvzEncodeResult.Success>());
        first.ToArray().ShouldBe(second.ToArray());
        if (!packJunk)
            a.ShouldBeOfType<RvzEncodeResult.Success>().PackedGroups.ShouldBe(0);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(first.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).ShouldBe(original);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ChunkJunkHeaderAndEofRanges_MatchIndependentIso()
    {
        const int chunk = 0x8000;
        byte[] original = GameCubeIsoFixture.Create(chunk * 7 + 103);
        GameCubeIsoFixture.AddPadding(original, 129, chunk * 2 - 149);
        Array.Clear(original, chunk * 4, chunk);
        using var output = new MemoryStream();
        (await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 12, chunk), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        var random = new Random(53);
        long[] boundaries = [0, 127, 128, 129, chunk - 1, chunk, chunk + 1,
            chunk * 2 - 20, chunk * 4 - 1, chunk * 5 - 1, original.Length - 1, original.Length, original.Length + 19];
        byte[] buffer = new byte[chunk + 17];
        for (int i = 0; i < 200 + boundaries.Length; i++)
        {
            long offset = i < boundaries.Length ? boundaries[i] : random.NextInt64(original.Length + 33L);
            int requested = i < boundaries.Length ? buffer.Length : random.Next(1, buffer.Length);
            Array.Fill(buffer, (byte)0xEE);

            int read = await decoded.ReadAtAsync(offset, buffer.AsMemory(0, requested), CancellationToken.None);

            int expected = (int)Math.Min(requested, Math.Max(0, original.Length - offset));
            read.ShouldBe(expected);
            buffer.AsSpan(0, read).ToArray().ShouldBe(original.AsSpan((int)Math.Min(offset, original.Length), expected).ToArray());
            buffer.AsSpan(read).IndexOfAnyExcept((byte)0xEE).ShouldBe(-1);
        }
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ZeroAndPartialGroups_UsesNoPayloadForZeroGroups()
    {
        const int chunk = 0x10000;
        byte[] original = GameCubeIsoFixture.Create(chunk * 3 + 901);
        Array.Clear(original, 128, original.Length - 128);
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, chunk), CancellationToken.None);

        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.ZeroGroups.ShouldBe(3);
        byte[] artifact = output.ToArray();
        int groupsAt = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 200)));
        artifact.AsSpan(groupsAt + 12, 36).IndexOfAnyExcept((byte)0).ShouldBe(-1);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(artifact), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).ShouldBe(original);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ShortSourceReads_FillsChunksWithoutWholeDiscRead()
    {
        const int chunk = 0x10000;
        byte[] original = GameCubeIsoFixture.Create(chunk * 2 + 13);
        var source = new TrackedContent(original) { MaximumRead = 97 };
        using var output = new MemoryStream();

        (await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, chunk), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();

        source.LargestRequest.ShouldBe(chunk);
        source.Reads.ShouldBeGreaterThan(original.Length / 97);
        source.Disposed.ShouldBeFalse();
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).ShouldBe(original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task EncodeGameCubeAsync_Cancellation_ClearsOutputAndPreservesCallerOwnership(int cancelAtRead)
    {
        using var cancelled = new CancellationTokenSource();
        using var output = new MemoryStream();
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x30000))
        {
            BeforeRead = (reads, _, _) =>
            {
                if (reads == cancelAtRead)
                {
                    output.Length.ShouldBeGreaterThan(0x124);
                    cancelled.Cancel();
                }
            }
        };
        if (cancelAtRead == 0)
            cancelled.Cancel();

        var error = await Should.ThrowAsync<OperationCanceledException>(() => new RvzEncoder().EncodeGameCubeAsync(source,
            output, new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x10000), cancelled.Token));

        error.CancellationToken.ShouldBe(cancelled.Token);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        output.CanWrite.ShouldBeTrue();
        source.Disposed.ShouldBeFalse();
        if (cancelAtRead == 0)
            source.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_CancelledAtFinalFlush_ClearsEvenPatchedHeader()
    {
        using var cancelled = new CancellationTokenSource();
        using var output = new CancelAtFlushStream(cancelled);

        await Should.ThrowAsync<OperationCanceledException>(() => new RvzEncoder().EncodeGameCubeAsync(
            new MemoryContent(GameCubeIsoFixture.Create(0x10000 + 1)), output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), cancelled.Token));

        output.BytesAtFlush.ShouldBeGreaterThan(0x124);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EncodeGameCubeAsync_TruncatedOrChangedSource_ReturnsFailureAndClearsOutput(bool changeHeader)
    {
        byte[] original = GameCubeIsoFixture.Create(0x10000 + 17);
        var source = new TrackedContent(original)
        {
            LengthOverride = changeHeader ? null : original.Length + 91,
            BeforeRead = (reads, _, _) => { if (changeHeader && reads == 2) original[0] ^= 1; }
        };
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeGameCubeAsync_SourceLengthChangesAfterLastRead_RejectsArtifact()
    {
        byte[] original = GameCubeIsoFixture.Create(0x10000 + 17);
        var source = new TrackedContent(original);
        source.BeforeRead = (reads, _, _) => { if (reads == 3) source.LengthOverride = original.Length + 1; };
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x10000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_MinimalLogicalDisc_PreservesItsPartialRawGroup()
    {
        byte[] original = GameCubeIsoFixture.Create(129);
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 22, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Success>().Groups.ShouldBe(1);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(0), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).ShouldBe(original);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_SourceFrameCorruption_IsTypedButOutputCorruptionPropagates()
    {
        var corruptSource = new TrackedContent(GameCubeIsoFixture.Create(0x10000))
        { BeforeRead = (_, _, _) => throw new InvalidDataException("bad source frame") };
        using var output = new MemoryStream();

        var failure = await new RvzEncoder().EncodeGameCubeAsync(corruptSource, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None);

        failure.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        output.Length.ShouldBe(0);
        using var corruptOutput = new FailAtWriteStream(new InvalidDataException("output failure"), 2);
        var error = await Should.ThrowAsync<InvalidDataException>(() => new RvzEncoder().EncodeGameCubeAsync(
            new MemoryContent(GameCubeIsoFixture.Create(0x10000)), corruptOutput,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None));
        error.Message.ShouldBe("output failure");
        corruptOutput.Length.ShouldBe(0);
        corruptOutput.Position.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_InputOrOutputIoFailure_PropagatesAndClearsOutput()
    {
        var failingSource = new TrackedContent(GameCubeIsoFixture.Create(0x10000))
        { BeforeRead = (reads, _, _) => { if (reads == 3) throw new IOException("input failed"); } };
        using var output = new MemoryStream();

        var inputError = await Should.ThrowAsync<IOException>(() => new RvzEncoder().EncodeGameCubeAsync(failingSource,
            output, new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None));

        inputError.Message.ShouldBe("input failed");
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        failingSource.Disposed.ShouldBeFalse();
        using var failingOutput = new FailAtWriteStream(new IOException("output failed"), 2);
        var outputError = await Should.ThrowAsync<IOException>(() => new RvzEncoder().EncodeGameCubeAsync(
            new MemoryContent(GameCubeIsoFixture.Create(0x10000)), failingOutput,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None));
        outputError.Message.ShouldBe("output failed");
        failingOutput.Length.ShouldBe(0);
        failingOutput.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 1, 0x8000)]
    [InlineData(5, 0, 0x8000)]
    [InlineData(5, 23, 0x8000)]
    [InlineData(99, 3, 0x8000)]
    [InlineData(5, 3, 0)]
    [InlineData(5, 3, 0x4000)]
    [InlineData(5, 3, 0xC000)]
    [InlineData(5, 3, 0x300000)]
    [InlineData(5, 3, 0xA00000)]
    public async Task EncodeGameCubeAsync_InvalidRecipe_IsTypedBeforeReadingOrAllocating(int compression, int level, int chunk)
    {
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x10000));
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions((RvzEncodeCompression)compression, level, chunk), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidOptions);
        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(17L << 30, 16L << 30)]
    [InlineData(128, 16L << 30)]
    [InlineData(16L << 30, 0x123)]
    [InlineData(16L << 30, 65L << 30)]
    public async Task EncodeGameCubeAsync_InvalidResourceCeilings_IsTypedBeforeReading(long logical, long artifact)
    {
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x10000));
        using var output = new MemoryStream();
        var options = new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 3, 0x8000)
        { MaxLogicalBytes = logical, MaxOutputBytes = artifact };

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output, options, CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidOptions);
        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData((16L << 30) + 1)]
    public async Task EncodeGameCubeAsync_OversizedDeclaredSource_RejectsBeforeSizeDependentAllocation(long length)
    {
        var source = new TrackedContent([]) { LengthOverride = length };
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 22, 0x800000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.LogicalLimitExceeded);
        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(-1)]
    public async Task EncodeGameCubeAsync_TruncatedDeclaredHeader_IsTypedWithoutReading(long length)
    {
        var source = new TrackedContent([]) { LengthOverride = length };
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeGameCubeAsync_WrongOrWiiMagic_ReturnsUnsupportedWithoutPublishing(bool wii)
    {
        byte[] original = GameCubeIsoFixture.Create(0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(original.AsSpan(wii ? 0x18 : 0x1C), wii ? 0x5D1C9EA3 : 0u);
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.UnsupportedMedia);
        output.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeGameCubeAsync_GroupOrTableExceedsOutputLimit_ClearsPartialArtifact(bool failAtTable)
    {
        const int chunk = 0x8000;
        byte[] original = GameCubeIsoFixture.Create(chunk * 3);
        if (failAtTable)
            Array.Clear(original, chunk, chunk * 2);
        var options = new RvzEncodeOptions(RvzEncodeCompression.None, 0, chunk)
        { PackJunk = false, MaxOutputBytes = 0x124 + chunk + (failAtTable ? 1 : 0) };
        using var output = new MemoryStream();

        var result = await new RvzEncoder().EncodeGameCubeAsync(new MemoryContent(original), output, options, CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.OutputLimitExceeded);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_NonemptyOutput_IsCallerErrorAndPreservesItsBytes()
    {
        using var output = new MemoryStream();
        output.Write("preserve"u8);
        byte[] before = output.ToArray();
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x10000));

        await Should.ThrowAsync<ArgumentException>(() => new RvzEncoder().EncodeGameCubeAsync(
            source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None));

        output.ToArray().ShouldBe(before);
        output.Position.ShouldBe(before.Length);
        source.Reads.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ReadOnlyOutput_RejectsWithoutSourceReadOrDestinationMutation()
    {
        byte[] original = "preserve"u8.ToArray();
        using var output = new MemoryStream(original, writable: false) { Position = 3 };

        await AssertRejectedDestinationAsync(output, original, 3, CancellationToken.None);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_NonSeekableOutput_RejectsWithoutSourceReadOrDestinationMutation()
    {
        using var output = new NonSeekableOutputStream();
        output.Write("preserve"u8);
        output.Position = 2;

        await AssertRejectedDestinationAsync(output, "preserve"u8.ToArray(), 2, CancellationToken.None);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_EmptyOutputWithNonzeroPosition_RejectsWithoutSourceReadOrDestinationMutation()
    {
        using var output = new MemoryStream { Position = 7 };

        await AssertRejectedDestinationAsync(output, [], 7, CancellationToken.None);
    }

    [Fact]
    public async Task EncodeGameCubeAsync_DestinationCannotTruncate_RejectsBeforeSourceReadOrMutation()
    {
        var cause = new NotSupportedException("destination does not support truncation");
        using var output = new NonTruncatableOutputStream(cause);
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x10000));
        var options = new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000)
        { MaxOutputBytes = 0x124, PackJunk = false };

        var rejection = await Should.ThrowAsync<NotSupportedException>(() =>
            new RvzEncoder().EncodeGameCubeAsync(source, output, options, CancellationToken.None));

        rejection.ShouldBeSameAs(cause);
        source.Reads.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
        output.WriteCalls.ShouldBe(0);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        output.TruncateCalls.ShouldBe(1);
        output.CanWrite.ShouldBeTrue();
    }

    private static async Task AssertRejectedDestinationAsync(MemoryStream output, byte[] original,
        long position, CancellationToken ct)
    {
        var source = new TrackedContent(GameCubeIsoFixture.Create(0x10000));

        await Should.ThrowAsync<ArgumentException>(() => new RvzEncoder().EncodeGameCubeAsync(source,
            output, new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), ct));

        source.Reads.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
        output.ToArray().ShouldBe(original);
        output.Length.ShouldBe(original.Length);
        output.Position.ShouldBe(position);
    }

    private static async Task<byte[]> ReadAllAsync(RvzImage image, CancellationToken ct)
    {
        byte[] bytes = new byte[checked((int)image.Length)];
        int position = 0;
        while (position < bytes.Length)
        {
            int take = Math.Min(17171, bytes.Length - position);
            (await image.ReadAtAsync(position, bytes.AsMemory(position, take), ct)).ShouldBe(take);
            position += take;
        }
        return bytes;
    }

    private sealed class TrackedContent(byte[] bytes) : IRandomAccessContent
    {
        public long? LengthOverride { get; set; }
        public int MaximumRead { get; init; } = int.MaxValue;
        public Action<int, long, CancellationToken>? BeforeRead { get; set; }
        public int Reads { get; private set; }
        public int LargestRequest { get; private set; }
        public bool Disposed { get; private set; }
        public long Length => LengthOverride ?? bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ct.ThrowIfCancellationRequested();
            Reads++;
            BeforeRead?.Invoke(Reads, offset, ct);
            ct.ThrowIfCancellationRequested();
            LargestRequest = Math.Max(LargestRequest, destination.Length);
            if (offset >= bytes.Length)
                return ValueTask.FromResult(0);
            int count = (int)Math.Min(Math.Min(destination.Length, MaximumRead), bytes.Length - offset);
            bytes.AsMemory((int)offset, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NonSeekableOutputStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    private sealed class NonTruncatableOutputStream(NotSupportedException cause) : MemoryStream
    {
        public int WriteCalls { get; private set; }
        public int TruncateCalls { get; private set; }

        public override void SetLength(long value)
        {
            TruncateCalls++;
            throw cause;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, ct);
        }
    }

    private sealed class FailAtWriteStream(Exception failure, int failAt) : MemoryStream
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ++_writes == failAt ? ValueTask.FromException(failure) : base.WriteAsync(buffer, ct);
    }

    private sealed class CancelAtFlushStream(CancellationTokenSource cancelled) : MemoryStream
    {
        public long BytesAtFlush { get; private set; }
        public override Task FlushAsync(CancellationToken ct)
        {
            BytesAtFlush = Length;
            cancelled.Cancel();
            return Task.CompletedTask;
        }
    }
}
