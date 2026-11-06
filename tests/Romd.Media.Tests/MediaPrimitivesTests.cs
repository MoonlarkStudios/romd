using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using SharpCompress.Compressors.LZMA;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class MediaPrimitivesTests
{
    [Fact]
    public void PaddingGenerator_RandomSeedsAndOffsets_MatchesTheSpecificationReference()
    {
        var random = new Random(23);
        var generator = new LaggedFibonacciGenerator();
        for (int i = 0; i < 100; i++)
        {
            byte[] seed = new byte[LaggedFibonacciGenerator.SeedBytes];
            random.NextBytes(seed);
            int skip = random.Next(0, 0x8000), length = random.Next(1, 20_000);
            byte[] actual = new byte[length];

            generator.Seed(seed);
            generator.Skip(skip);
            generator.Generate(actual);

            actual.ShouldBe(ReferenceDisc.Padding(seed, skip, length), $"case {i}");
        }
    }

    [Fact]
    public void Lzma_DeclaredDictionaryLargerThanTheOutput_AllocatesOnlyOutputSizedState()
    {
        byte[] data = new byte[0x20000];
        new Random(3).NextBytes(data.AsSpan(0, 0x10000));
        var compressed = new MemoryStream();
        byte[] properties;
        using (var encoder = LzmaStream.Create(new LzmaEncoderProperties(eos: true, dictionary: 64 << 20), false, compressed))
        {
            properties = encoder.Properties!;
            encoder.Write(data);
        }
        byte[] stored = compressed.ToArray();
        using var codec = RvzCodec.Create(RvzCodec.Lzma, properties)!;
        byte[] output = new byte[0x30000];
        codec.Decompress(stored, stored.Length, output); // warm up JIT-time allocations

        long before = GC.GetAllocatedBytesForCurrentThread();
        int written = codec.Decompress(stored, stored.Length, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        written.ShouldBe(data.Length);
        output.AsSpan(0, written).SequenceEqual(data).ShouldBeTrue();
        allocated.ShouldBeLessThan(2L << 20); // not the declared 64 MiB dictionary
    }

    [Theory]
    [InlineData(RvzMethod.Zstd)]
    [InlineData(RvzMethod.Lzma)]
    [InlineData(RvzMethod.Bzip2)]
    public async Task Decompress_OutputLargerThanTheGroupLimit_IsInvalid(RvzMethod method)
    {
        // A table claiming fewer entries than it decompresses to must be rejected, not truncated.
        var file = Discs.GameCube(method: method).File.ToArray();
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(0x48 + 196));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x48 + 196), count - 1);
        System.Security.Cryptography.SHA1.HashData(file.AsSpan(0x48, 0xDC)).CopyTo(file.AsSpan(0x10));
        System.Security.Cryptography.SHA1.HashData(file.AsSpan(0, 0x34)).CopyTo(file.AsSpan(0x34));

        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }
}
