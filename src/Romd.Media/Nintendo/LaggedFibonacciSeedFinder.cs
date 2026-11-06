using System.Buffers.Binary;

namespace Romd.Media.Nintendo;

/// <summary>Inverts the generator's XOR state updates, then proves the recovered seed against the bytes.</summary>
internal sealed class LaggedFibonacciSeedFinder
{
    private const int Words = 521, Lag = 32, StateBytes = Words * 4;
    private readonly uint[] _state = new uint[Words];
    private readonly byte[] _comparison = new byte[StateBytes];
    private readonly LaggedFibonacciGenerator _generator = new();

    public int Find(ReadOnlySpan<byte> data, int offsetInBlock, Span<byte> seed)
    {
        int skip = (4 - offsetInBlock % 4) % 4;
        if (data.Length - skip < StateBytes)
            return 0;
        var window = data.Slice(skip, StateBytes);
        // The output's second byte repeats two bits from the first byte. Random data almost
        // always fails this filter before we invert any state. This alone never proves a run.
        for (int word = 0; word < Words; word++)
            if ((window[word * 4 + 1] >> 6) != (window[word * 4] & 3))
                return 0;

        // Beyond one state, x[n] = x[n-521] XOR x[n-32]. Reject misleading byte-layout
        // candidates cheaply rather than inverting a full state at every word of a poison run.
        int following = Math.Min(4, (data.Length - skip - StateBytes) / 4);
        for (int word = 0; word < following; word++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(skip + StateBytes + word * 4)..]) !=
                (BinaryPrimitives.ReadUInt32LittleEndian(window[(word * 4)..]) ^
                 BinaryPrimitives.ReadUInt32LittleEndian(window[((Words - Lag + word) * 4)..])))
                return 0;

        int wordOffset = (offsetInBlock + skip) / 4;
        int position = wordOffset % Words;
        for (int word = 0; word < Words; word++)
            _state[(position + word) % Words] = BinaryPrimitives.ReadUInt32LittleEndian(window[(word * 4)..]);
        Reverse(position); // The window's wrapped prefix belongs to the next generator state.
        for (int round = 0; round < wordOffset / Words + 4; round++)
            Reverse(Words);

        Span<uint> original = stackalloc uint[Words];
        for (int word = 0; word < Words; word++)
            original[word] = BinaryPrimitives.ReverseEndianness(_state[word]);
        for (int word = 0; word < 17; word++)
        {
            // Emitting x>>18 loses bits 16/17. The initialization recurrence exposes them in
            // bits 7/8 of (x[i+16] XOR x[i+15]); the x[i-1]<<23 term cannot affect those bits.
            // Word 0 is only used as x[0]<<23, so its discarded bits never affect any output.
            // Normalize those two unknowable bits to zero for deterministic equivalent seeds.
            uint missing = word == 0 ? 0 : ((original[word + 16] ^ original[word + 15]) << 9) & 0x00030000;
            uint recovered = (original[word] & 0xff00ffff) | ((original[word] << 2) & 0x00fc0000) | missing;
            BinaryPrimitives.WriteUInt32BigEndian(seed[(word * 4)..], recovered);
        }
        _generator.Seed(seed);
        _generator.Skip(offsetInBlock);
        int matched = 0;
        while (matched < data.Length)
        {
            int take = Math.Min(_comparison.Length, data.Length - matched);
            _generator.Generate(_comparison.AsSpan(0, take));
            int equal = data.Slice(matched, take).CommonPrefixLength(_comparison.AsSpan(0, take));
            matched += equal;
            if (equal != take)
                break;
        }
        return matched;
    }

    private void Reverse(int count)
    {
        for (int word = count - 1; word >= 0; word--)
            _state[word] ^= _state[word < Lag ? word + Words - Lag : word - Lag];
    }
}
