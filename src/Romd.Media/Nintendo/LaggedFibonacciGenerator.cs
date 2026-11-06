using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Romd.Media.Nintendo;

/// <summary>
/// GameCube/Wii padding generator: a lagged Fibonacci generator (f = xor, j = 32, k = 521), as
/// specified in Dolphin's docs/WiaAndRvz.md. Words are stored pre-arranged so output is a plain
/// little-endian copy of the state; the arrangement is XOR-linear, so it commutes with advancing.
/// </summary>
internal sealed class LaggedFibonacciGenerator
{
    public const int SeedBytes = SeedWords * 4;
    private const int K = 521, J = 32, SeedWords = 17, StateBytes = K * 4;
    private readonly uint[] _state = new uint[K];
    private int _position;

    public LaggedFibonacciGenerator()
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Media decoding requires a little-endian host.");
    }

    public void Seed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedBytes)
            throw new ArgumentException("A padding seed is 68 bytes.", nameof(seed));
        var state = _state.AsSpan();
        for (int i = 0; i < SeedWords; i++)
            state[i] = BinaryPrimitives.ReadUInt32BigEndian(seed[(i * 4)..]);
        for (int i = SeedWords; i < K; i++)
            state[i] = (state[i - 17] << 23) ^ (state[i - 16] >> 9) ^ state[i - 1];
        // Each word emits x>>24, x>>18, x>>8, x: store that byte sequence as a little-endian word.
        for (int i = 0; i < K; i++)
        {
            uint word = state[i];
            state[i] = BinaryPrimitives.ReverseEndianness((word & 0xFF00FFFF) | ((word >> 2) & 0x00FF0000));
        }
        for (int i = 0; i < 4; i++)
            Advance();
        _position = 0;
    }

    public void Skip(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _position += bytes;
        while (_position >= StateBytes)
        {
            Advance();
            _position -= StateBytes;
        }
    }

    public void Generate(Span<byte> output)
    {
        var state = MemoryMarshal.AsBytes(_state.AsSpan());
        while (!output.IsEmpty)
        {
            int take = Math.Min(output.Length, StateBytes - _position);
            state.Slice(_position, take).CopyTo(output);
            output = output[take..];
            _position += take;
            if (_position == StateBytes)
            {
                Advance();
                _position = 0;
            }
        }
    }

    // Every word reads a word at least J = 32 positions back, so four-lane vectors never read a
    // lane written in the same step.
    private void Advance()
    {
        ref uint words = ref MemoryMarshal.GetArrayDataReference(_state);
        int lanes = Vector128<uint>.Count, i = 0;
        for (; i < J; i += lanes)
            (Vector128.LoadUnsafe(ref words, (nuint)i) ^ Vector128.LoadUnsafe(ref words, (nuint)(i + K - J)))
                .StoreUnsafe(ref words, (nuint)i);
        for (; i + lanes <= K; i += lanes)
            (Vector128.LoadUnsafe(ref words, (nuint)i) ^ Vector128.LoadUnsafe(ref words, (nuint)(i - J)))
                .StoreUnsafe(ref words, (nuint)i);
        for (; i < K; i++)
            _state[i] ^= _state[i - J];
    }
}

/// <summary>RVZ packing: runs of stored bytes and seeded padding, decoded to an exact length.</summary>
internal static class RvzPacking
{
    [ThreadStatic] private static LaggedFibonacciGenerator? t_generator;

    /// <param name="dataOffset">Offset of <paramref name="output"/>'s first byte: disc offset for raw data,
    /// hash-free partition data offset for Wii partitions. Only its position within 32 KiB matters.</param>
    public static void Unpack(ReadOnlySpan<byte> packed, Span<byte> output, long dataOffset)
    {
        var generator = t_generator ??= new LaggedFibonacciGenerator();
        int input = 0, written = 0;
        while (input < packed.Length)
        {
            if (packed.Length - input < 4)
                throw new InvalidDataException("Truncated RVZ packing header.");
            uint header = BinaryPrimitives.ReadUInt32BigEndian(packed[input..]);
            input += 4;
            int size = (int)(header & 0x7FFFFFFF);
            if (size > output.Length - written)
                throw new InvalidDataException("RVZ packing exceeds its chunk.");
            if ((header & 0x80000000) != 0)
            {
                if (packed.Length - input < LaggedFibonacciGenerator.SeedBytes)
                    throw new InvalidDataException("Truncated RVZ padding seed.");
                generator.Seed(packed.Slice(input, LaggedFibonacciGenerator.SeedBytes));
                input += LaggedFibonacciGenerator.SeedBytes;
                generator.Skip((int)((dataOffset + written) % 0x8000));
                generator.Generate(output.Slice(written, size));
            }
            else
            {
                if (packed.Length - input < size)
                    throw new InvalidDataException("Truncated RVZ packed data.");
                packed.Slice(input, size).CopyTo(output[written..]);
                input += size;
            }
            written += size;
        }
        if (written != output.Length)
            throw new InvalidDataException("RVZ packing is shorter than its chunk.");
    }
}
