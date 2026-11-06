using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzWiiEncoderTests
{
    [Theory]
    [InlineData(RvzEncodeCompression.None, 0, 0x8000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x10000)]
    [InlineData(RvzEncodeCompression.Zstandard, 19, 0x20000)]
    [InlineData(RvzEncodeCompression.Zstandard, 12, 0x200000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x400000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x600000)]
    [InlineData(RvzEncodeCompression.Zstandard, 1, 0x800000)]
    public async Task EncodeAsync_AllChunkShapesAndPartialFinalRegion_ReconstructsCompleteHashes(
        RvzEncodeCompression compression, int level, int chunk)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0);
        using var output = new MemoryStream();
        var source = new MemoryContent(fixture.Iso);

        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(source, output,
            new RvzEncodeOptions(compression, level, chunk), CancellationToken.None);

        var success = result.ShouldBeOfType<RvzEncodeResult.Success>();
        success.LogicalBytes.ShouldBe(fixture.Iso.Length);
        success.ArtifactBytes.ShouldBe(output.Length);
        source.Disposed.ShouldBeFalse();
        output.Position.ShouldBe(output.Length);
        VerifyEmbeddedKeys(output.ToArray(), fixture);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(16L << 20), ct: CancellationToken.None);
        decoded.Descriptor.DiscType.ShouldBe(2u);
        decoded.Descriptor.HasWiiPartitions.ShouldBeTrue();
        decoded.Descriptor.Compression.ShouldBe((uint)compression);
        decoded.Descriptor.CompressionLevel.ShouldBe(level);
        decoded.Descriptor.ChunkSize.ShouldBe(chunk);
        byte[] actual = await ReadAllAsync(decoded, CancellationToken.None);
        actual.AsSpan().SequenceEqual(fixture.Iso).ShouldBeTrue("complete encrypted ISO equality");
        SHA256.HashData(actual).ShouldBe(SHA256.HashData(fixture.Iso));
        SHA1.HashData(actual).ShouldBe(SHA1.HashData(fixture.Iso));
        MD5.HashData(actual).ShouldBe(MD5.HashData(fixture.Iso));
        Crc32.Hash(actual).ShouldBe(Crc32.Hash(fixture.Iso));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EncodeAsync_FixedRetailAndKoreanTicketVectors_StoresExpectedKeyAndRecoversKnownBootFst(byte index)
    {
        var vector = WiiTicketVectors.Get(index);
        var fixture = SyntheticWiiIsoBuilder.Create(vector, sectors: 65);
        using var output = new MemoryStream();

        (await new RvzWiiEncoder().EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 19, 0x20000), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();

        byte[] artifact = output.ToArray();
        VerifyEmbeddedKeys(artifact, fixture);
        int table = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 152)));
        using var extractor = new WiiPartitionExtractor(artifact.AsSpan(table, 16));
        var partition = fixture.Partitions[0];
        (await extractor.ReadRegionAsync(new MemoryContent(fixture.Iso), partition.DataOffset, 64, CancellationToken.None)).ShouldBeTrue();
        byte[] plain = new byte[64 * 0x7C00];
        extractor.CopyPayload(0, 64, plain);
        plain.AsSpan(0, 0x440).ToArray().ShouldBe(partition.Plain.AsSpan(0, 0x440).ToArray());
        plain.AsSpan(0x1000, 0x100).ToArray().ShouldBe(partition.Plain.AsSpan(0x1000, 0x100).ToArray());
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(artifact), new RvzReadOptions(),
            new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).AsSpan().SequenceEqual(fixture.Iso)
            .ShouldBeTrue("complete fixed-vector ISO equality across a full region and partial tail");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EncodeAsync_ConfiguredTicketIndex_StoresExpectedKeyAndRecoversKnownBootFst(byte index)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(index == 0 ? standard : korean, index, sectors: 2);
        using var output = new MemoryStream();
        (await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 19, 0x20000), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();
        byte[] artifact = output.ToArray();
        VerifyEmbeddedKeys(artifact, fixture);
        int table = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 152)));
        using var extractor = new WiiPartitionExtractor(artifact.AsSpan(table, 16));
        var part = fixture.Partitions[0];

        (await extractor.ReadRegionAsync(new MemoryContent(fixture.Iso), part.DataOffset, part.Sectors, CancellationToken.None)).ShouldBeTrue();

        byte[] plain = new byte[part.Plain.Length];
        extractor.CopyPayload(0, part.Sectors, plain);
        plain.AsSpan(0, 0x440).ToArray().ShouldBe(part.Plain.AsSpan(0, 0x440).ToArray());
        plain.AsSpan(0x1000, 0x100).ToArray().ShouldBe(part.Plain.AsSpan(0x1000, 0x100).ToArray());
    }

    [Theory]
    [InlineData(RvzEncodeCompression.None, 0, 0x20000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x20000)]
    [InlineData(RvzEncodeCompression.None, 0, 0x200000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x200000)]
    [InlineData(RvzEncodeCompression.None, 0, 0x400000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x400000)]
    [InlineData(RvzEncodeCompression.None, 0, 0x600000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x600000)]
    [InlineData(RvzEncodeCompression.None, 0, 0x800000)]
    [InlineData(RvzEncodeCompression.Zstandard, 3, 0x800000)]
    public async Task EncodeAsync_MaximumHashExceptions_BoundsEveryListAndPartialTailAndPreservesPadding(
        RvzEncodeCompression compression, int level, int chunk)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        int sectorsPerChunk = chunk / 0x8000, lists = Math.Max(1, chunk / 0x200000);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: Math.Max(65, sectorsPerChunk + 1), poisonHashes: true);
        using var output = new MemoryStream();

        (await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(compression, level, chunk) { PackJunk = false }, CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();

        byte[] artifact = output.ToArray();
        VerifyExceptionLists(artifact, compression, chunk, first: true, sectorsPerChunk,
            Enumerable.Repeat(52 * Math.Min(64, sectorsPerChunk), lists).ToArray());
        int[] tailCounts = new int[lists];
        tailCounts[0] = 52;
        VerifyExceptionLists(artifact, compression, chunk, first: false, sectors: 1, tailCounts);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(artifact), new RvzReadOptions(),
            new MediaBlockCache(16L << 20), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).AsSpan().SequenceEqual(fixture.Iso)
            .ShouldBeTrue("all hash exceptions, including every list, partial tail and overlapping padding windows");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_MultiplePartitionDirectoryShapes_PreservesRawMetadataGapsAndTailDeterministically(bool oneDirectoryGroup)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(korean, 1, sectors: 3, partitions: 4, oneDirectoryGroup: oneDirectoryGroup);
        var encoder = new RvzWiiEncoder(new WiiTicket(standard, korean));
        var options = new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 15, 0x10000);
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        var a = await encoder.EncodeAsync(new MemoryContent(fixture.Iso), first, options, CancellationToken.None);
        var b = await encoder.EncodeAsync(new MemoryContent(fixture.Iso), second, options, CancellationToken.None);

        a.ShouldBeOfType<RvzEncodeResult.Success>().ShouldBe(b.ShouldBeOfType<RvzEncodeResult.Success>());
        first.ToArray().AsSpan().SequenceEqual(second.ToArray()).ShouldBeTrue("deterministic Wii artifact");
        VerifyEmbeddedKeys(first.ToArray(), fixture);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(first.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).AsSpan().SequenceEqual(fixture.Iso)
            .ShouldBeTrue("all partition types in the selected directory shape, and complete raw gaps/tail");
    }

    [Fact]
    public async Task EncodeAsync_ZeroDataWithCanonicalHashes_UsesZeroGroupsWithoutInventingExceptions()
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, zeroDataAfterFirstRegion: true);
        using var output = new MemoryStream();

        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 3, 0x200000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Success>().ZeroGroups.ShouldBe(2);
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        (await ReadAllAsync(decoded, CancellationToken.None)).AsSpan().SequenceEqual(fixture.Iso).ShouldBeTrue("zero partition data groups");
    }

    [Fact]
    public async Task EncodeAsync_RangesAcrossRawPartitionRegionJunkAndEof_MatchIso()
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(korean, 1, sectors: 65);
        using var output = new MemoryStream();
        (await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 19, 0x20000), CancellationToken.None))
            .ShouldBeOfType<RvzEncodeResult.Success>();
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(output.ToArray()), new RvzReadOptions(),
            new MediaBlockCache(8L << 20), ct: CancellationToken.None);
        long start = fixture.Partitions[0].DataOffset;
        long[] boundaries = [127, 128, start - 1, start, start + 0x8000 - 1,
            start + 0x20000 - 1, start + 0x200000 - 1, start + 65 * 0x8000 - 1, fixture.Iso.Length - 1,
            fixture.Iso.Length, fixture.Iso.Length + 19L];
        byte[] buffer = new byte[0x10001];
        var random = new Random(131);
        for (int i = 0; i < boundaries.Length + 100; i++)
        {
            long at = i < boundaries.Length ? boundaries[i] : random.NextInt64(fixture.Iso.Length + 23L);
            int take = (int)Math.Min(buffer.Length, Math.Max(0, fixture.Iso.Length - at));
            Array.Fill(buffer, (byte)0xA5);

            (await decoded.ReadAtAsync(at, buffer, CancellationToken.None)).ShouldBe(take);

            buffer.AsSpan(0, take).SequenceEqual(fixture.Iso.AsSpan((int)Math.Min(at, fixture.Iso.Length), take))
                .ShouldBeTrue("Wii range equality");
            buffer.AsSpan(take).IndexOfAnyExcept((byte)0xA5).ShouldBe(-1);
        }
    }

    [Theory]
    [InlineData("count")]
    [InlineData("table")]
    [InlineData("data_start")]
    [InlineData("data_size")]
    [InlineData("unaligned")]
    [InlineData("partial_sector")]
    [InlineData("h3")]
    [InlineData("metadata_overlap")]
    [InlineData("partition_overlap")]
    public async Task EncodeAsync_MalformedLayout_IsTypedAndClearsOutput(string problem)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1, partitions: 2);
        int first = (int)fixture.Partitions[0].Offset;
        switch (problem)
        {
            case "count":
                U32(fixture.Iso, 0x40000, 65);
                break;
            case "table":
                U32(fixture.Iso, 0x40004, uint.MaxValue);
                break;
            case "data_start":
                U32(fixture.Iso, first + 0x2B8, uint.MaxValue);
                break;
            case "data_size":
                U32(fixture.Iso, first + 0x2BC, uint.MaxValue);
                break;
            case "unaligned":
                U32(fixture.Iso, first + 0x2B8, 0x20004 / 4);
                break;
            case "partial_sector":
                U32(fixture.Iso, first + 0x2BC, 0x7FFC / 4);
                break;
            case "h3":
                U32(fixture.Iso, first + 0x2B4, 0);
                break;
            case "metadata_overlap":
                U32(fixture.Iso, first + 0x2B0, 0x2C0 / 4);
                break;
            case "partition_overlap":
                U32(fixture.Iso, 0x40220, (uint)(first / 4));
                break;
        }
        using var output = new MemoryStream();

        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.Zstandard, 3, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData("hashless")]
    [InlineData("plain")]
    [InlineData("empty")]
    [InlineData("signature")]
    [InlineData("key_index")]
    public async Task EncodeAsync_RecognizedUnsupportedProfile_ReturnsExplicitUnsupported(string problem)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1);
        int first = (int)fixture.Partitions[0].Offset;
        switch (problem)
        {
            case "hashless":
                fixture.Iso[0x60] = 1;
                break;
            case "plain":
                fixture.Iso[0x61] = 1;
                break;
            case "empty":
                Array.Clear(fixture.Iso, 0x40000, 32);
                break;
            case "signature":
                U32(fixture.Iso, first, 0x00010000);
                break;
            case "key_index":
                fixture.Iso[first + 0x1F1] = 2;
                break;
        }
        using var output = new MemoryStream();

        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.UnsupportedMedia);
        output.Length.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeAsync_CancellationAfterAdmission_ClearsPartialOutputAndPreservesSource()
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1);
        using var output = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        int reads = 0;
        var source = new MemoryContent(fixture.Iso)
        {
            BeforeRead = _ =>
            {
                if (++reads == 5)
                { output.Length.ShouldBeGreaterThan(0); cancelled.Cancel(); }
                return Task.CompletedTask;
            }
        };

        var error = await Should.ThrowAsync<OperationCanceledException>(() => new RvzWiiEncoder(new WiiTicket(standard, korean))
            .EncodeAsync(source, output, new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), cancelled.Token));

        error.CancellationToken.ShouldBe(cancelled.Token);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_OutputLimit_IsTypedAndClearsHeader()
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1);
        using var output = new MemoryStream();

        var result = await new RvzWiiEncoder(new WiiTicket(standard, korean)).EncodeAsync(new MemoryContent(fixture.Iso), output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000) { MaxOutputBytes = 0x124 }, CancellationToken.None);

        result.ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.OutputLimitExceeded);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_SourceFrameOrIoFailure_PreservesClassificationAndClearsPartialOutput(bool frameCorruption)
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1);
        using var output = new MemoryStream();
        int reads = 0;
        Exception cause = frameCorruption ? new InvalidDataException("source frame corruption") : new IOException("source I/O failure");
        var source = new MemoryContent(fixture.Iso)
        {
            BeforeRead = _ =>
            {
                if (++reads == 5)
                { output.Length.ShouldBeGreaterThan(0); throw cause; }
                return Task.CompletedTask;
            }
        };
        var encoder = new RvzWiiEncoder(new WiiTicket(standard, korean));
        var options = new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000);

        if (frameCorruption)
            (await encoder.EncodeAsync(source, output, options, CancellationToken.None))
                .ShouldBeOfType<RvzEncodeResult.Failure>().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        else
            (await Should.ThrowAsync<IOException>(() => encoder.EncodeAsync(source, output, options, CancellationToken.None)))
                .ShouldBeSameAs(cause);

        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_OutputInvalidDataFailure_PropagatesAndClearsPartialOutput()
    {
        byte[] standard = WiiTicketTests.SyntheticKey(41), korean = WiiTicketTests.SyntheticKey(47);
        var fixture = SyntheticWiiIsoBuilder.Create(standard, 0, sectors: 1);
        var cause = new InvalidDataException("output failure");
        using var output = new FailAtWriteStream(cause);

        (await Should.ThrowAsync<InvalidDataException>(() => new RvzWiiEncoder(new WiiTicket(standard, korean))
            .EncodeAsync(new MemoryContent(fixture.Iso), output,
                new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None))).ShouldBeSameAs(cause);

        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    [Fact]
    public async Task EncodeAsync_UntruncatableDestination_RejectsBeforeAnySourceRead()
    {
        var source = new MemoryContent([]);
        using var output = new NonTruncatableStream();

        await Should.ThrowAsync<NotSupportedException>(() => new RvzWiiEncoder(new WiiTicket(WiiTicketTests.SyntheticKey(41),
            WiiTicketTests.SyntheticKey(47))).EncodeAsync(source, output,
            new RvzEncodeOptions(RvzEncodeCompression.None, 0, 0x8000), CancellationToken.None));

        source.Reads.ShouldBe(0);
        output.Length.ShouldBe(0);
        output.Position.ShouldBe(0);
    }

    private static void VerifyExceptionLists(byte[] artifact, RvzEncodeCompression compression, int chunk,
        bool first, int sectors, int[] expectedCounts)
    {
        int tableAt = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 200)));
        int tableSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 208)));
        int groups = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 196)));
        byte[] table = new byte[checked(groups * 12)];
        using var codec = RvzCodec.Create((uint)compression, ReadOnlySpan<byte>.Empty);
        if (codec is null)
        {
            tableSize.ShouldBe(table.Length);
            artifact.AsSpan(tableAt, tableSize).CopyTo(table);
        }
        else
            codec.Decompress(artifact.AsSpan(tableAt, tableSize).ToArray(), tableSize, table).ShouldBe(table.Length);
        int partitionAt = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 152)));
        int firstGroup = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(partitionAt + 24)));
        int groupCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(partitionAt + 28)));
        groupCount.ShouldBeGreaterThan(1);
        int entry = checked((firstGroup + (first ? 0 : groupCount - 1)) * 12);
        int at = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(entry)) * 4L));
        uint sizeAndFlag = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(entry + 4));
        bool compressed = (sizeAndFlag & 0x80000000u) != 0;
        int size = checked((int)(sizeAndFlag & 0x7FFFFFFFu));
        BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(entry + 8)).ShouldBe(0u);
        byte[] data = new byte[checked(sectors * 0x7C00 + expectedCounts.Sum(count => 2 + count * 22) + 3)];
        int decodedBytes;
        if (compressed)
        {
            codec.ShouldNotBeNull();
            decodedBytes = codec.Decompress(artifact.AsSpan(at, size).ToArray(), size, data);
        }
        else
        {
            size.ShouldBeLessThanOrEqualTo(data.Length);
            artifact.AsSpan(at, size).CopyTo(data);
            decodedBytes = size;
        }
        int prefix = 0;
        foreach (int expected in expectedCounts)
        {
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(prefix)).ShouldBe((ushort)expected);
            prefix = checked(prefix + 2 + expected * 22);
        }
        int alignment = compressed ? 0 : (4 - prefix % 4) % 4;
        data.AsSpan(prefix, alignment).IndexOfAnyExcept((byte)0).ShouldBe(-1);
        decodedBytes.ShouldBe(prefix + alignment + sectors * 0x7C00);
        if (compression == RvzEncodeCompression.None)
        {
            compressed.ShouldBeFalse();
            if (first && chunk >= 0x200000)
                size.ShouldBeGreaterThan(chunk, "maximum exception prefix exceeds the declared chunk-sized GC buffer");
            if (first && chunk == 0x600000)
                alignment.ShouldBe(2, "alignment is applied once to all three exception lists together");
        }
    }

    private static void VerifyEmbeddedKeys(byte[] artifact, SyntheticWiiIso fixture)
    {
        int at = checked((int)BinaryPrimitives.ReadUInt64BigEndian(artifact.AsSpan(0x48 + 152)));
        BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(0x48 + 144)).ShouldBe((uint)fixture.Partitions.Count);
        for (int i = 0; i < fixture.Partitions.Count; i++)
            artifact.AsSpan(at + i * 48, 16).SequenceEqual(fixture.Partitions[i].ExpectedTitleKey)
                .ShouldBeTrue("embedded partition key equals independently expected synthetic key");
    }
    private static async Task<byte[]> ReadAllAsync(RvzImage image, CancellationToken ct)
    {
        byte[] result = new byte[checked((int)image.Length)];
        int at = 0;
        while (at < result.Length)
        {
            int count = Math.Min(29413, result.Length - at);
            (await image.ReadAtAsync(at, result.AsMemory(at, count), ct)).ShouldBe(count);
            at += count;
        }
        return result;
    }
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
    private sealed class FailAtWriteStream(Exception failure) : MemoryStream
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            ++_writes == 2 ? ValueTask.FromException(failure) : base.WriteAsync(bytes, ct);
    }
    private sealed class NonTruncatableStream : MemoryStream
    {
        public override void SetLength(long value) => throw new NotSupportedException("destination cannot truncate");
    }
}
