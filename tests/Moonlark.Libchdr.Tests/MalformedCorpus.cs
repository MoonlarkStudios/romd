using System.Buffers.Binary;

namespace Moonlark.Libchdr.Tests;

/// <summary>Builds a deterministic corpus of malformed CHDs from every chdman fixture and crafted v1–v4 files.</summary>
/// <remarks>Families follow the specification's list: truncation, corrupted headers and maps, metadata cycles and
/// oversized sizes, parent references, unknown codecs, and seeded random flips, plus crafted maps libchdr mishandles,
/// deep and forward self-reference chains, and v3/v4 offsets at the 64-bit wrap. Every input is synthetic. The corpus
/// finds defects the runner can observe: crashes, hangs, overruns, and output that depends on prior memory,
/// read-ahead or read order. Validation rules whose removal no runner check can observe are guarded by
/// ChdMapValidationTests instead.</remarks>
internal static class MalformedCorpus
{
    private const int V5HeaderBytes = 124;
    private const int V4HeaderBytes = 108;
    private const int V3HeaderBytes = 120;
    private const int V2HeaderBytes = 80;
    private const int V1HeaderBytes = 76;
    private const int RandomFlipsPerSource = 16;
    private const int LongChain = 60_000;

    private static readonly string[] Fixtures =
        ["dvd-none", "dvd-zlib", "dvd-lzma", "dvd-huff", "dvd-flac", "dvd-zstd", "dvd-zstd-hs4096", "cd-cdlz", "cd-cdzl", "cd-cdfl", "cd-cdzs",
            "cd-subcode"];

    internal static IEnumerable<(string Name, byte[] Bytes)> Build()
    {
        foreach (string fixture in Fixtures)
        {
            byte[] original = File.ReadAllBytes(NativeTestEnvironment.Fixture(fixture + ".chd"));
            foreach ((string family, byte[] bytes) in V5Mutations(original))
                yield return ($"{fixture}-{family}", bytes);
        }
        byte[] v4 = CraftedChd.V4(1, CraftedChd.LegacyEntry.Deflated, CraftedChd.LegacyEntry.Stored, CraftedChd.LegacyEntry.Self(0));
        foreach ((string family, byte[] bytes) in LegacyMutations(v4, V4HeaderBytes, seed: 4))
            yield return ($"v4-{family}", bytes);
        foreach ((string family, byte[] bytes) in LegacyMutations(V3(v4), V3HeaderBytes, seed: 3))
            yield return ($"v3-{family}", bytes);
        byte[] v2 = CraftedChd.V2(1, true, true);
        foreach ((string family, byte[] bytes) in OldLegacyMutations(v2, V2HeaderBytes, seed: 2))
            yield return ($"v2-{family}", bytes);
        foreach ((string family, byte[] bytes) in OldLegacyMutations(V1(v2), V1HeaderBytes, seed: 1))
            yield return ($"v1-{family}", bytes);
        foreach ((string name, byte[] bytes) in CraftedMaps())
            yield return ($"crafted-{name}", bytes);
    }

    /// <summary>Well-formed containers whose maps libchdr mishandles natively; random flips rarely reach these, because the
    /// v5 map is CRC-protected.</summary>
    private static IEnumerable<(string, byte[])> CraftedMaps()
    {
        CraftedChd.V5Entry stored = CraftedChd.V5Entry.Stored;
        CraftedChd.LegacyEntry legacy = CraftedChd.LegacyEntry.Stored;
        yield return ("v5-unknown-type", CraftedChd.V5(stored, new(14)));
        yield return ("v5-self-cycle", CraftedChd.V5(stored, CraftedChd.V5Entry.Self(1)));
        yield return ("v5-self-two-cycle", CraftedChd.V5(stored, CraftedChd.V5Entry.Self(2), CraftedChd.V5Entry.Self(1)));
        yield return ("v5-parent", CraftedChd.V5(stored, new(CraftedChd.V5Parent)));
        yield return ("v5-stored-hunk-beyond-24-bits", CraftedChd.V5Sized(1 << 24, "zlib", stored));
        // libchdr reads the truncated length, zero bytes, and checks the CRC of the untouched destination; with a CRC of
        // zeros the read succeeds only after a zero prefill, which the runner's separate prefill outcomes catch.
        yield return ("v5-stored-hunk-beyond-24-bits-crc-of-zeros", CraftedChd.V5Zeroed(1 << 24, "zlib", stored));
        yield return ("v5-av-codec", CraftedChd.V5Sized(CraftedChd.HunkBytes, "avhu", stored));
        yield return ("v4-self-cycle", CraftedChd.V4(0, legacy, CraftedChd.LegacyEntry.Self(1)));
        yield return ("v4-parent", CraftedChd.V4(0, legacy, new(CraftedChd.LegacyParent)));
        yield return ("v4-compressed-without-codec", CraftedChd.V4(0, legacy, new(0x10 | CraftedChd.LegacyCompressed)));
        yield return ("v4-av-codec", CraftedChd.V4(3, legacy));
        yield return ("v4-fourcc-codec", CraftedChd.V4(0x7A737464, legacy));
        yield return ("v2-compressed-without-codec", CraftedChd.V2(0, true, false));
        yield return ("v5-empty-codec-slot", CraftedChd.V5(stored, new(1)));
        // MINI writes eight bytes whatever the hunk size; the runner's canary catches the overrun.
        for (int hunkBytes = 1; hunkBytes < 8; hunkBytes++)
            yield return ($"v4-mini-hunk-of-{hunkBytes}", CraftedChd.V4Sized(hunkBytes, 0, CraftedChd.LegacyEntry.Mini(0x0102030405060708)));
        // Hunk n references n - 1 (backward) or n + 1 (forward). Read in reverse, or forward from hunk 0, libchdr recurses
        // once per hop, so an accepted long chain overflows the runner's small stack.
        foreach (int depth in (int[])[17, LongChain])
        {
            ulong[] backward = [.. Enumerable.Range(0, depth).Select(target => (ulong)target)];
            ulong[] forward = [.. Enumerable.Range(1, depth).Select(target => (ulong)target)];
            yield return ($"v5-self-chain-{depth}", CraftedChd.V5([stored, .. backward.Select(CraftedChd.V5Entry.Self)]));
            yield return ($"v5-self-forward-chain-{depth}", CraftedChd.V5([.. forward.Select(CraftedChd.V5Entry.Self), stored]));
            yield return ($"v4-self-chain-{depth}", CraftedChd.V4Sized(16, 0, [legacy, .. backward.Select(CraftedChd.LegacyEntry.Self)]));
            yield return ($"v4-self-forward-chain-{depth}", CraftedChd.V4Sized(16, 0, [.. forward.Select(CraftedChd.LegacyEntry.Self), legacy]));
        }
    }

    private static IEnumerable<(string, byte[])> V5Mutations(byte[] original)
    {
        foreach ((string family, byte[] bytes) in Truncations(original))
            yield return (family, bytes);
        (string Field, int Offset, int Width, ulong[] Values)[] fields =
        [
            ("length", 8, 4, [0, 123, 125, uint.MaxValue]),
            ("version", 12, 4, [0, 4, 6, uint.MaxValue]),
            ("codec0", 16, 4, [0, 0x78787878, 0x61766875, 1]),
            ("codec1", 20, 4, [0x78787878, 0x61766875]),
            ("logical", 32, 8, [0, 1, ulong.MaxValue, long.MaxValue, 1UL << 40]),
            ("map", 40, 8, [0, 1, (ulong)original.Length, (ulong)original.Length - 1, ulong.MaxValue]),
            ("meta", 48, 8, [1, 123, (ulong)original.Length - 1, ulong.MaxValue]),
            ("hunk", 56, 4, [0, 1, 7, 8, (128U << 20) + 1, uint.MaxValue]),
            ("unit", 60, 4, [0, 1, uint.MaxValue]),
        ];
        foreach ((string field, int offset, int width, ulong[] values) in fields)
            foreach (ulong value in values)
                yield return ($"header-{field}-{value:x}", With(original, bytes => Write(bytes, offset, width, value)));
        yield return ("raw-sha1", With(original, bytes => bytes[64] ^= 0x5A));
        yield return ("overall-sha1", With(original, bytes => bytes[84] ^= 0x5A));
        yield return ("parent-sha1", With(original, bytes => bytes.AsSpan(104, 20).Fill(0x5A)));
        int map = (int)BinaryPrimitives.ReadUInt64BigEndian(original.AsSpan(40));
        if (map > 0 && map + 16 <= original.Length)
        {
            int span = Math.Min(16 + (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(map)), Math.Min(64, original.Length - map));
            for (int index = 0; index < span; index++)
                yield return ($"map-byte-{index}", With(original, bytes => bytes[map + index] ^= 0xFF));
        }
        ulong meta = BinaryPrimitives.ReadUInt64BigEndian(original.AsSpan(48));
        if (meta >= V5HeaderBytes && meta + 16 <= (ulong)original.Length)
        {
            int entry = (int)meta;
            yield return ("meta-self-cycle", With(original, bytes => Write(bytes, entry + 8, 8, meta)));
            yield return ("meta-next-outside", With(original, bytes => Write(bytes, entry + 8, 8, (ulong)original.Length + 1)));
            yield return ("meta-next-header", With(original, bytes => Write(bytes, entry + 8, 8, 1)));
            yield return ("meta-length-huge", With(original, bytes => Write(bytes, entry + 4, 4, 0x01FFFFFF)));
            yield return ("meta-tag", With(original, bytes => Write(bytes, entry, 4, 0x78787878)));
        }
        foreach ((string family, byte[] bytes) in RandomFlips(original, seed: original.Length))
            yield return (family, bytes);
    }

    /// <summary>Mutations of a three-entry v3 or v4 file with 16-byte map entries after a <paramref name="header"/>-byte header.</summary>
    private static IEnumerable<(string, byte[])> LegacyMutations(byte[] original, int header, int seed)
    {
        foreach ((string family, byte[] bytes) in Truncations(original))
            yield return (family, bytes);
        for (int entry = 0; entry < 3; entry++)
        {
            int at = header + entry * 16;
            yield return ($"entry{entry}-offset-huge", With(original, bytes => Write(bytes, at, 8, ulong.MaxValue)));
            yield return ($"entry{entry}-length-huge", With(original, bytes => { bytes[at + 12] = 0xFF; bytes[at + 13] = 0xFF; bytes[at + 14] = 0xFF; }));
            for (int flags = 0; flags < 0x20; flags += 3)
                yield return ($"entry{entry}-flags-{flags:x2}", With(original, bytes => bytes[at + 15] = (byte)flags));
        }
        // Offsets whose end wraps past 2^64 for the deflated entry 0 or the stored entry 1, and offsets just short of it.
        foreach (int entry in (int[])[0, 1])
            foreach (ulong below in (ulong[])[15, 4095, 4096, 1 << 24])
            {
                int at = header + entry * 16;
                yield return ($"entry{entry}-offset-wrap-minus-{below}", With(original, bytes => Write(bytes, at, 8, ulong.MaxValue - below)));
            }
        yield return ("cookie", With(original, bytes => bytes[header + 48] ^= 0xFF));
        yield return ("parent-flag", With(original, bytes => Write(bytes, 16, 4, 1)));
        yield return ("totalhunks-huge", With(original, bytes => Write(bytes, 24, 4, uint.MaxValue)));
        foreach ((string family, byte[] bytes) in RandomFlips(original, seed))
            yield return (family, bytes);
    }

    /// <summary>Mutations of a v1 or v2 file with 8-byte map entries (44-bit offset, 20-bit length) after a
    /// <paramref name="header"/>-byte header.</summary>
    private static IEnumerable<(string, byte[])> OldLegacyMutations(byte[] original, int header, int seed)
    {
        foreach ((string family, byte[] bytes) in Truncations(original))
            yield return (family, bytes);
        int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(28));
        for (int entry = 0; entry < entries; entry++)
        {
            int at = header + entry * 8;
            ulong raw = BinaryPrimitives.ReadUInt64BigEndian(original.AsSpan(at));
            yield return ($"entry{entry}-offset-max", With(original, bytes => Write(bytes, at, 8, raw | 0xFFFFFFFFFFFUL)));
            yield return ($"entry{entry}-length-zero", With(original, bytes => Write(bytes, at, 8, raw & 0xFFFFFFFFFFFUL)));
            yield return ($"entry{entry}-length-max", With(original, bytes => Write(bytes, at, 8, raw | 0xFFFFFUL << 44)));
        }
        yield return ("cookie", With(original, bytes => bytes[header + entries * 8] ^= 0xFF));
        yield return ("totalhunks-huge", With(original, bytes => Write(bytes, 28, 4, uint.MaxValue)));
        foreach ((string family, byte[] bytes) in RandomFlips(original, seed))
            yield return (family, bytes);
    }

    /// <summary>The v3 form of a v4 file: a 120-byte header with MD5 fields, the same map, and data offsets moved with it.</summary>
    private static byte[] V3(byte[] v4)
    {
        const int shift = V3HeaderBytes - V4HeaderBytes;
        byte[] v3 = new byte[v4.Length + shift];
        v4.AsSpan(0, 44).CopyTo(v3);
        BinaryPrimitives.WriteUInt32BigEndian(v3.AsSpan(8), V3HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(v3.AsSpan(12), 3);
        v4.AsSpan(44, 4).CopyTo(v3.AsSpan(76));
        v4.AsSpan(48, 40).CopyTo(v3.AsSpan(80));
        v4.AsSpan(V4HeaderBytes).CopyTo(v3.AsSpan(V3HeaderBytes));
        uint hunks = BinaryPrimitives.ReadUInt32BigEndian(v4.AsSpan(24));
        for (int entry = 0; entry < hunks; entry++)
        {
            Span<byte> raw = v3.AsSpan(V3HeaderBytes + entry * 16, 16);
            if ((raw[15] & 0x0F) is 1 or 2)
                BinaryPrimitives.WriteUInt64BigEndian(raw, BinaryPrimitives.ReadUInt64BigEndian(raw) + shift);
        }
        return v3;
    }

    /// <summary>The v1 form of a v2 file: a 76-byte header without the sector length, which v1 fixes at 512 bytes.</summary>
    private static byte[] V1(byte[] v2)
    {
        const int shift = V2HeaderBytes - V1HeaderBytes;
        byte[] v1 = new byte[v2.Length - shift];
        v2.AsSpan(0, V1HeaderBytes).CopyTo(v1);
        BinaryPrimitives.WriteUInt32BigEndian(v1.AsSpan(8), V1HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(v1.AsSpan(12), 1);
        v2.AsSpan(V2HeaderBytes).CopyTo(v1.AsSpan(V1HeaderBytes));
        uint hunks = BinaryPrimitives.ReadUInt32BigEndian(v2.AsSpan(28));
        for (int entry = 0; entry < hunks; entry++)
        {
            Span<byte> raw = v1.AsSpan(V1HeaderBytes + entry * 8, 8);
            BinaryPrimitives.WriteUInt64BigEndian(raw, BinaryPrimitives.ReadUInt64BigEndian(raw) - shift);
        }
        return v1;
    }

    private static IEnumerable<(string, byte[])> Truncations(byte[] original)
    {
        int[] lengths = [0, 7, 15, 16, 64, 123, 124, 140, 200, original.Length / 4, original.Length / 2, original.Length * 3 / 4, original.Length - 1];
        foreach (int length in lengths.Where(length => length < original.Length).Distinct())
            yield return ($"truncated-{length}", original[..length]);
    }

    private static IEnumerable<(string, byte[])> RandomFlips(byte[] original, int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < RandomFlipsPerSource; index++)
        {
            int at = random.Next(original.Length);
            byte mask = (byte)random.Next(1, 256);
            yield return ($"flip-{at}-{mask:x2}", With(original, bytes => bytes[at] ^= mask));
        }
    }

    private static byte[] With(byte[] original, Action<byte[]> change)
    {
        byte[] copy = [.. original];
        change(copy);
        return copy;
    }

    private static void Write(byte[] bytes, int offset, int width, ulong value)
    {
        if (width == 4)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)value);
        else
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), value);
    }
}
