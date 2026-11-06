using System.Buffers.Binary;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzPackingEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(2083)]
    [InlineData(2084)]
    [InlineData(10000)]
    [InlineData(30000)]
    public void Find_ReferencePaddingAtArbitraryOffset_RecoversSeedAndProvesEveryByte(int offset)
    {
        var finder = new LaggedFibonacciSeedFinder();
        var random = new Random(23);
        for (int i = 0; i < 8; i++)
        {
            byte[] seed = new byte[68], recovered = new byte[68];
            random.NextBytes(seed);
            byte[] expected = ReferenceDisc.Padding(seed, offset, Math.Min(12000, 0x8000 - offset));

            finder.Find(expected, offset, recovered).ShouldBe(expected.Length);

            BinaryPrimitives.WriteUInt32BigEndian(seed, BinaryPrimitives.ReadUInt32BigEndian(seed) & ~0x00030000u);
            recovered.ShouldBe(seed);
            ReferenceDisc.Padding(recovered, offset, expected.Length).ShouldBe(expected);
        }
    }

    [Fact]
    public void Find_RandomDataOrShortWindow_DoesNotClaimPadding()
    {
        var finder = new LaggedFibonacciSeedFinder();
        byte[] random = new byte[0x8000], seed = new byte[68];
        new Random(7).NextBytes(random);
        finder.Find(random, 0, seed).ShouldBe(0);
        finder.Find(new byte[2083], 0, seed).ShouldBe(0);
        finder.Find(new byte[2084], 1, seed).ShouldBe(0);
    }

    [Fact]
    public void Find_DamagedPadding_NeverOverstatesTheVerifiedPrefix()
    {
        byte[] seed = new byte[68], recovered = new byte[68];
        new Random(73).NextBytes(seed);
        byte[] damaged = ReferenceDisc.Padding(seed, 0, 0x8000);
        damaged[10000] ^= 1;

        int matched = new LaggedFibonacciSeedFinder().Find(damaged, 0, recovered);

        matched.ShouldBe(10000);
        ReferenceDisc.Padding(recovered, 0, matched).ShouldBe(damaged.AsSpan(0, matched).ToArray());
    }

    [Fact]
    public void Pack_LiteralAndJunkAcrossBlocks_ReconstructsExactBytesAndIsDeterministic()
    {
        byte[] input = new byte[0x18000];
        new Random(17).NextBytes(input);
        GameCubeIsoFixture.AddPadding(input, 123, 0x12000);
        input[10000] ^= 1; // A poisoned candidate byte remains literal; it is never guessed away.
        var packing = new RvzPackingEncoder();
        byte[] packed = new byte[input.Length], second = new byte[input.Length], output = new byte[input.Length];

        int size = packing.Pack(input, packed, 0, CancellationToken.None);
        int repeated = packing.Pack(input, second, 0, CancellationToken.None);
        RvzPacking.Unpack(packed.AsSpan(0, size), output, 0);

        size.ShouldBeGreaterThan(0);
        size.ShouldBeLessThan(input.Length);
        repeated.ShouldBe(size);
        second.AsSpan(0, repeated).ToArray().ShouldBe(packed.AsSpan(0, size).ToArray());
        output.ShouldBe(input);
    }

    [Fact]
    public void Pack_NonzeroDiscOffsetAndPartialBlock_UsesDiscRelativeSkip()
    {
        const int offset = 1234, length = 16384;
        byte[] seed = new byte[68];
        new Random(31).NextBytes(seed);
        byte[] input = ReferenceDisc.Padding(seed, offset % 0x8000, length);
        byte[] packed = new byte[length], output = new byte[length];

        int size = new RvzPackingEncoder().Pack(input, packed, offset, CancellationToken.None);

        size.ShouldBeGreaterThan(0);
        size.ShouldBeLessThan(input.Length);
        RvzPacking.Unpack(packed.AsSpan(0, size), output, offset);
        output.ShouldBe(input);
    }

    [Fact]
    public void Pack_MisleadingWordLayout_DoesNotInventASeededRun()
    {
        byte[] input = new byte[0x20000];
        new Random(37).NextBytes(input);
        for (int word = 0; word < input.Length / 4; word++)
            input[word * 4 + 1] = (byte)((input[word * 4 + 1] & 63) | ((input[word * 4] & 3) << 6));

        new RvzPackingEncoder().Pack(input, new byte[input.Length], 0, CancellationToken.None).ShouldBe(0);
    }

    [Fact]
    public void Find_RecurrenceConformingArbitraryState_DoesNotProveASeededRun()
    {
        byte[] input = RecurrencePoison(0x8000), seed = new byte[68];
        var finder = new LaggedFibonacciSeedFinder();
        int[] offsets = [0, 4, 2084, 10000, 30000];
        foreach (int offset in offsets)
        {
            Array.Fill(seed, (byte)0xA5);

            int matched = finder.Find(input.AsSpan(offset), offset, seed);

            // Both cheap filters pass this fixture. A changed seed confirms the full state
            // inversion ran; a byte-proven run still cannot be recovered from arbitrary state.
            seed.AsSpan().IndexOfAnyExcept((byte)0xA5).ShouldBeGreaterThanOrEqualTo(0);
            matched.ShouldBeLessThan(seed.Length);
            ReferenceDisc.Padding(seed, offset, matched).ShouldBe(input.AsSpan(offset, matched).ToArray());
        }
    }

    [Fact]
    public void Pack_RecurrenceConformingArbitraryState_DoesNotInventSeededPadding()
    {
        byte[] input = RecurrencePoison(0x20000);

        new RvzPackingEncoder().Pack(input, new byte[input.Length], 0, CancellationToken.None).ShouldBe(0);
    }

    [Fact]
    public void Pack_RandomData_DoesNotAddLiteralHeaders()
    {
        byte[] input = new byte[0x20000];
        new Random(9).NextBytes(input);
        new RvzPackingEncoder().Pack(input, new byte[input.Length], 0, CancellationToken.None).ShouldBe(0);
    }

    private static byte[] RecurrencePoison(int length)
    {
        const int words = 521, lag = 32;
        byte[] bytes = new byte[length];
        new Random(61).NextBytes(bytes.AsSpan(0, words * 4));
        for (int word = 0; word < words; word++)
            bytes[word * 4 + 1] = (byte)((bytes[word * 4 + 1] & 63) | ((bytes[word * 4] & 3) << 6));
        // Start from a full arbitrary state, without the generator's 17-word seeded
        // initialization. XOR preserves the output-byte relation, and every continuation
        // obeys the lagged recurrence, so the production's cheap rejection filters pass.
        for (int word = words; word < length / 4; word++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((word - words) * 4)) ^
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((word - lag) * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word * 4), value);
        }
        return bytes;
    }

    [Fact]
    public void Pack_PreCancelledToken_StopsBeforeScanning()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() =>
            new RvzPackingEncoder().Pack(new byte[0x8000], new byte[0x8000], 0, cancelled.Token));
    }
}
