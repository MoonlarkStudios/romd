using System.Buffers.Binary;
using System.IO.Compression;

namespace Moonlark.Libchdr.Tests;

/// <summary>Builds minimal synthetic CHDs whose maps exercise libchdr's per-entry handling.</summary>
/// <remarks>Stored hunks are uncompressed and filled with <see cref="Fill"/>, so readers can be checked
/// without a codec. Every map is otherwise well formed, including its CRC and end-of-list cookie.</remarks>
internal static class CraftedChd
{
    internal const int HunkBytes = 4096;
    internal const byte V5Self = 5;
    internal const byte V5Parent = 6;
    internal const byte LegacyCompressed = 0x01;
    internal const byte LegacyStored = 0x12;
    internal const byte LegacyMini = 0x13;
    internal const byte LegacySelf = 0x04;
    internal const byte LegacyParent = 0x05;
    internal const byte LegacyDeflated = 0x11;

    private const byte V5None = 4;
    private const int V5HeaderBytes = 124;
    private const int V4HeaderBytes = 108;
    private const int V2HeaderBytes = 80;

    /// <summary>A v5 entry: a stored hunk, or a type with its self/parent target.</summary>
    internal readonly record struct V5Entry(byte Type, ulong Target = 0)
    {
        internal static V5Entry Stored => new(V5None);
        internal static V5Entry Self(ulong target) => new(V5Self, target);
    }

    /// <summary>A v3/v4 entry: a stored or raw-deflated hunk, a MINI value, or raw flags with an offset field.</summary>
    internal readonly record struct LegacyEntry(byte Flags, ulong Offset = 0)
    {
        internal static LegacyEntry Stored => new(LegacyStored);
        internal static LegacyEntry Deflated => new(LegacyDeflated);
        internal static LegacyEntry Mini(ulong value) => new(LegacyMini, value);
        internal static LegacyEntry Self(ulong target) => new(LegacySelf, target);
    }

    /// <summary>The fill byte of stored hunk <paramref name="index"/>.</summary>
    internal static byte Fill(int index) => (byte)(0x11 * (index + 1));

    /// <summary>A v5 file with a Huffman-coded map and zlib in codec slot 0 only.</summary>
    internal static byte[] V5(params V5Entry[] entries) => V5Sized(HunkBytes, "zlib", entries);

    /// <summary>A v5 file with the given hunk size and slot-0 codec FourCC.</summary>
    internal static byte[] V5Sized(int hunkBytes, string codec, params V5Entry[] entries) => V5Filled(hunkBytes, codec, Fill, entries);

    /// <summary>A v5 file whose stored hunks hold only zero bytes, so their CRC is the CRC of an all-zero hunk.</summary>
    internal static byte[] V5Zeroed(int hunkBytes, string codec, params V5Entry[] entries) => V5Filled(hunkBytes, codec, _ => 0, entries);

    private static byte[] V5Filled(int hunkBytes, string codec, Func<int, byte> fill, V5Entry[] entries)
    {
        // libchdr's bit reader refills only up to 24 buffered bits, so wider fields misdecode; chdman sizes them to the map.
        const int selfBits = 24, parentBits = 8, lengthBits = 16;
        var bits = new BitWriter();
        for (int code = 0; code < 16; code++)
            bits.Write(4, 4);
        foreach (V5Entry entry in entries)
            bits.Write(entry.Type, 4);
        foreach ((V5Entry entry, int index) in entries.Select((entry, index) => (entry, index)))
        {
            if (entry.Type == V5None)
                bits.Write(Crc16(StoredHunk(hunkBytes, fill(index))), 16);
            else if (entry.Type <= 3)
            { bits.Write(0, lengthBits); bits.Write(0, 16); }
            else if (entry.Type == V5Self)
                bits.Write((uint)entry.Target, selfBits);
            else if (entry.Type == V5Parent)
                bits.Write((uint)entry.Target, parentBits);
        }
        byte[] packed = bits.ToArray();
        int mapOffset = V5HeaderBytes;
        int firstOffset = mapOffset + 16 + packed.Length;
        int stored = entries.Count(entry => entry.Type == V5None);
        byte[] file = new byte[firstOffset + stored * hunkBytes];
        byte[] rawMap = new byte[entries.Length * 12];
        ulong current = (ulong)firstOffset;
        for (int index = 0; index < entries.Length; index++)
        {
            Span<byte> raw = rawMap.AsSpan(index * 12, 12);
            V5Entry entry = entries[index];
            raw[0] = entry.Type;
            if (entry.Type == V5None)
            {
                byte[] data = StoredHunk(hunkBytes, fill(index));
                data.CopyTo(file, (int)current);
                // libchdr decodes the length into 24 bits, so the map CRC covers the truncated value.
                WriteUInt24(raw[1..], (uint)hunkBytes);
                WriteUInt48(raw[4..], current);
                BinaryPrimitives.WriteUInt16BigEndian(raw[10..], Crc16(data));
                current += (ulong)hunkBytes;
            }
            else
                WriteUInt48(raw[4..], entry.Type is V5Self or V5Parent ? entry.Target : current);
        }
        "MComprHD"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), V5HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 5);
        System.Text.Encoding.ASCII.GetBytes(codec).CopyTo(file.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(32), (ulong)entries.Length * (ulong)hunkBytes);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(40), (ulong)mapOffset);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(60), 2048);
        Span<byte> mapHeader = file.AsSpan(mapOffset, 16);
        BinaryPrimitives.WriteUInt32BigEndian(mapHeader, (uint)packed.Length);
        WriteUInt48(mapHeader[4..], (ulong)firstOffset);
        BinaryPrimitives.WriteUInt16BigEndian(mapHeader[10..], Crc16(rawMap));
        mapHeader[12] = lengthBits;
        mapHeader[13] = selfBits;
        mapHeader[14] = parentBits;
        packed.CopyTo(file, mapOffset + 16);
        return file;
    }

    /// <summary>A v4 file whose header names codec <paramref name="codec"/> (0 = none).</summary>
    internal static byte[] V4(uint codec, params LegacyEntry[] entries) => V4Sized(HunkBytes, codec, entries);

    /// <summary>A v4 file with the given hunk size. Stored entries hold their fill; deflated ones raw deflate of it.</summary>
    internal static byte[] V4Sized(int hunkBytes, uint codec, params LegacyEntry[] entries)
    {
        int dataOffset = V4HeaderBytes + (entries.Length + 1) * 16;
        bool anyData = entries.Any(entry => entry.Flags is LegacyStored or LegacyDeflated);
        byte[] file = new byte[dataOffset + (anyData ? entries.Length * hunkBytes : 0)];
        "MComprHD"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), V4HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), codec);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(24), (uint)entries.Length);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(28), (ulong)entries.Length * (ulong)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(44), (uint)hunkBytes);
        for (int index = 0; index < entries.Length; index++)
        {
            Span<byte> raw = file.AsSpan(V4HeaderBytes + index * 16, 16);
            LegacyEntry entry = entries[index];
            int data = dataOffset + index * hunkBytes;
            byte[]? payload = entry.Flags switch
            {
                LegacyStored => StoredHunk(index, hunkBytes),
                LegacyDeflated => Deflate(StoredHunk(index, hunkBytes)),
                _ => null,
            };
            payload?.CopyTo(file, data);
            int length = payload?.Length ?? 16;
            BinaryPrimitives.WriteUInt64BigEndian(raw, payload is null ? entry.Offset : (ulong)data);
            BinaryPrimitives.WriteUInt16BigEndian(raw[12..], (ushort)length);
            raw[14] = (byte)(length >> 16);
            raw[15] = entry.Flags;
        }
        "EndOfListCookie\0"u8.CopyTo(file.AsSpan(V4HeaderBytes + entries.Length * 16));
        return file;
    }

    /// <summary>Moves v4 entry <paramref name="index"/> to <paramref name="offset"/> in place, keeping its length, CRC and
    /// flags, and returns the same array.</summary>
    internal static byte[] WithV4Offset(byte[] v4, int index, ulong offset)
    {
        BinaryPrimitives.WriteUInt64BigEndian(v4.AsSpan(V4HeaderBytes + index * 16), offset);
        return v4;
    }

    /// <summary>A v2 file whose 8-byte entries are stored when their length equals a hunk.</summary>
    internal static byte[] V2(uint codec, params bool[] storedEntries)
    {
        const int sectorBytes = 512;
        int dataOffset = V2HeaderBytes + (storedEntries.Length + 1) * 8;
        byte[] file = new byte[dataOffset + storedEntries.Length * HunkBytes];
        "MComprHD"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), V2HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), codec);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(24), HunkBytes / sectorBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(28), (uint)storedEntries.Length);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(40), (uint)(storedEntries.Length * HunkBytes / sectorBytes));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(76), sectorBytes);
        for (int index = 0; index < storedEntries.Length; index++)
        {
            int data = dataOffset + index * HunkBytes;
            ulong length = storedEntries[index] ? HunkBytes : 16UL;
            if (storedEntries[index])
                StoredHunk(index, HunkBytes).CopyTo(file, data);
            BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(V2HeaderBytes + index * 8), length << 44 | (uint)data);
        }
        "EndOfLis"u8.CopyTo(file.AsSpan(V2HeaderBytes + storedEntries.Length * 8));
        return file;
    }

    private static byte[] StoredHunk(int index, int hunkBytes) => StoredHunk(hunkBytes, Fill(index));

    private static byte[] StoredHunk(int hunkBytes, byte fill) => Enumerable.Repeat(fill, hunkBytes).ToArray();

    /// <summary>Raw deflate, the stream format libchdr's legacy zlib codec inflates.</summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= (uint)value << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            crc &= 0xFFFF;
        }
        return (ushort)crc;
    }

    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }

    private static void WriteUInt48(Span<byte> destination, ulong value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(value >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)value);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _used = 8;

        internal void Write(uint value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                if (_used == 8)
                { _bytes.Add(0); _used = 0; }
                if (((value >> bit) & 1) != 0)
                    _bytes[^1] |= (byte)(0x80 >> _used);
                _used++;
            }
        }

        internal byte[] ToArray() => [.. _bytes];
    }
}
