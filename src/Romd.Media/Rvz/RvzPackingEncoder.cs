using System.Buffers.Binary;
using Romd.Media.Nintendo;

namespace Romd.Media.Rvz;

/// <summary>Only byte-proven seeded runs are replaced, and only when the packed chunk is smaller.</summary>
internal sealed class RvzPackingEncoder
{
    private const int BlockBytes = 0x8000, MinimumRunBytes = LaggedFibonacciGenerator.SeedBytes + 8;
    private readonly LaggedFibonacciSeedFinder _seeds = new();

    public int Pack(ReadOnlySpan<byte> input, Span<byte> output, long discOffset, CancellationToken ct)
    {
        int position = 0, literal = 0, written = 0;
        Span<byte> seed = stackalloc byte[LaggedFibonacciGenerator.SeedBytes];
        while (position < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            int inBlock = (int)((discOffset + position) % BlockBytes);
            int remaining = Math.Min(BlockBytes - inBlock, input.Length - position);
            int matched = _seeds.Find(input.Slice(position, remaining), inBlock, seed);
            if (matched <= MinimumRunBytes)
            {
                // Every output word is aligned to the disc. Starting a few bytes into a run
                // still recovers its seed, so scanning words does not need a byte-sized map.
                position += Math.Min(4, remaining);
                continue;
            }
            int literalBytes = position - literal;
            if (literalBytes > 0)
            {
                if (output.Length - written < 4 + literalBytes)
                    return 0;
                BinaryPrimitives.WriteUInt32BigEndian(output[written..], (uint)literalBytes);
                input.Slice(literal, literalBytes).CopyTo(output[(written + 4)..]);
                written += 4 + literalBytes;
            }
            if (output.Length - written < 4 + seed.Length)
                return 0;
            BinaryPrimitives.WriteUInt32BigEndian(output[written..], 0x80000000u | (uint)matched);
            seed.CopyTo(output[(written + 4)..]);
            written += 4 + seed.Length;
            position += matched;
            literal = position;
        }
        if (written == 0)
            return 0;
        int tail = input.Length - literal;
        if (tail > 0)
        {
            if (output.Length - written < 4 + tail)
                return 0;
            BinaryPrimitives.WriteUInt32BigEndian(output[written..], (uint)tail);
            input[literal..].CopyTo(output[(written + 4)..]);
            written += 4 + tail;
        }
        return written < input.Length ? written : 0;
    }
}
