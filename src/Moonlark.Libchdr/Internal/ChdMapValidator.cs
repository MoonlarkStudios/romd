using System.Buffers;
using System.Buffers.Binary;
using Moonlark.Libchdr.Interop;

namespace Moonlark.Libchdr.Internal;

/// <summary>Rejects codecs and hunk map entries the pinned libchdr would mishandle, before any hunk is read.</summary>
/// <remarks>
/// libchdr reports success without writing the destination for unknown entry types, v1–v4 compressed entries
/// without a codec and v5 stored entries whose 24-bit length truncates the hunk size; writes eight bytes for a v3/v4
/// MINI entry whatever the hunk size; follows self-references without a cycle bound; dereferences a missing parent for
/// v1–v4 parent entries; accepts any v5 codec in a v1–v4 header, where entries may carry no CRC; and its A/V decoder
/// can succeed with most of a hunk unwritten. Its v1–v4 open check and read-ahead window checks add an entry's offset and
/// length without a wrap guard, so an entry whose end wraps past 2^64 opens and copies from before the window. v5 is
/// checked on the map libchdr itself decoded; v1–v4 on the fixed-size entries libchdr read from the source, which must
/// not change while the file is open.
/// </remarks>
internal static unsafe class ChdMapValidator
{
    /// <summary>The longest self-reference chain accepted; chdman only references stored hunks, so its chains have one hop.</summary>
    internal const int MaxSelfReferenceDepth = 16;

    private const uint AvHuffman = 0x61766875;
    private const int V5EntryBytes = 12;
    private const byte V5LastCodecSlot = 3;
    private const byte V5Stored = 4;
    private const byte V5Self = 5;
    private const byte V5Parent = 6;

    private const uint LegacyZlib = 1;
    private const uint LegacyZlibPlus = 2;
    private const uint LegacyAv = 3;
    private const int LegacyTypeMask = 0x0F;
    private const int LegacyCompressed = 1;
    private const int LegacyUncompressed = 2;
    private const int LegacyMini = 3;
    private const int LegacySelf = 4;
    private const int LegacyParent = 5;
    private const int MiniBytes = 8;

    internal static void Validate(chd_header* header, ChdSourceContext source)
    {
        if (header->version >= 5)
            ValidateV5(header);
        else
            ValidateLegacy(header, source);
    }

    private static void ValidateV5(chd_header* header)
    {
        for (int slot = 0; slot < 4; slot++)
            if (header->compression[slot] == AvHuffman)
                throw Unsupported("the A/V Huffman codec");
        // An uncompressed v5 map holds only stored-hunk offsets, which libchdr reads in full or zero-fills.
        if (header->compression[0] == 0)
            return;
        if (header->mapentrybytes != V5EntryBytes || header->rawmap == null)
            throw new ChdValidationException(ChdError.InvalidState, "validate map: libchdr exposed no decoded map");
        var map = new V5Map(header->rawmap, header->hunkcount);
        for (uint index = 0; index < map.Count; index++)
        {
            byte type = map.Type(index);
            if (type <= V5LastCodecSlot)
            {
                if (header->compression[type] == 0)
                    throw Invalid(index, $"uses empty codec slot {type}");
            }
            else if (type == V5Stored)
            {
                if (map.Length(index) != header->hunkbytes)
                    throw Invalid(index, $"stores {map.Length(index)} bytes of a {header->hunkbytes}-byte hunk");
            }
            else if (type == V5Self)
                ResolveChain(map, index);
            else if (type == V5Parent)
                throw ParentEntry(index);
            else
                throw Invalid(index, $"has unknown entry type {type}");
        }
    }

    private static void ValidateLegacy(chd_header* header, ChdSourceContext source)
    {
        uint codec = header->compression[0];
        if (codec == LegacyAv)
            throw Unsupported("the A/V codec");
        if (codec is not (0 or LegacyZlib or LegacyZlibPlus))
            throw new ChdValidationException(ChdError.InvalidData, $"validate header: compression {codec:x8} is not a v1–v4 codec");
        int entryBytes = header->version < 3 ? 8 : 16;
        long mapBytes = (long)header->totalhunks * entryBytes;
        if (mapBytes > Array.MaxLength)
            throw new ChdValidationException(ChdError.NotSupported, "validate map: the legacy map exceeds the managed limit");
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)mapBytes);
        try
        {
            ChdMetadataIndex.ReadExactly(source, header->length, buffer.AsSpan(0, (int)mapBytes));
            var map = new LegacyMap(buffer, entryBytes, header->hunkbytes, header->totalhunks);
            ulong sourceBytes = (ulong)source.SourceLength;
            for (uint index = 0; index < map.Count; index++)
            {
                switch (map.Kind(index))
                {
                    case LegacyCompressed when codec == 0:
                        throw Invalid(index, "is compressed but the header names no codec");
                    case LegacyCompressed or LegacyUncompressed:
                        ValidateExtent(map, index, sourceBytes);
                        break;
                    case LegacyMini when header->hunkbytes < MiniBytes:
                        throw Invalid(index, $"is MINI, which writes {MiniBytes} bytes into a {header->hunkbytes}-byte hunk");
                    case LegacyMini:
                        break;
                    case LegacySelf:
                        ResolveChain(map, index);
                        break;
                    case LegacyParent:
                        throw ParentEntry(index);
                    default:
                        throw Invalid(index, $"has unknown entry type {map.Kind(index)}");
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>Applies libchdr's open check, that an entry's offset plus length lies within the source, without its wrap.</summary>
    private static void ValidateExtent(in LegacyMap map, uint index, ulong sourceBytes)
    {
        ulong offset = map.Target(index);
        uint length = map.Length(index);
        if (length > sourceBytes || offset > sourceBytes - length)
            throw new ChdValidationException(ChdError.InvalidFile,
                $"validate map hunk {index}: entry stores {length} bytes at offset {offset}, past the end of the {sourceBytes}-byte source");
    }

    /// <summary>Follows a self-reference to a hunk that is not one, within the depth bound; every hop is range checked.</summary>
    private static void ResolveChain<TMap>(in TMap map, uint index) where TMap : struct, IHunkMap
    {
        uint current = index;
        for (int depth = 1; depth <= MaxSelfReferenceDepth; depth++)
        {
            ulong target = map.Target(current);
            if (target >= map.Count)
                throw Invalid(index, $"references hunk {target} outside the map");
            current = (uint)target;
            if (!map.IsSelf(current))
                return;
        }
        throw Invalid(index, $"has a self-reference chain longer than {MaxSelfReferenceDepth}");
    }

    private static ChdValidationException Invalid(uint index, string detail) =>
        new(ChdError.InvalidData, $"validate map hunk {index}: entry {detail}");

    private static ChdValidationException ParentEntry(uint index) =>
        new(ChdError.InvalidData, $"validate map hunk {index}: entry references a parent the header does not declare");

    private static ChdValidationException Unsupported(string codec) =>
        new(ChdError.UnsupportedFormat, $"validate header: {codec} is not supported");

    private interface IHunkMap
    {
        uint Count { get; }
        bool IsSelf(uint index);
        ulong Target(uint index);
    }

    /// <summary>libchdr's decoded v5 map: type, 24-bit length, 48-bit offset or target, CRC16.</summary>
    private readonly struct V5Map(byte* entries, uint count) : IHunkMap
    {
        public uint Count => count;
        public bool IsSelf(uint index) => Type(index) == V5Self;
        public ulong Target(uint index) =>
            (ulong)BinaryPrimitives.ReadUInt16BigEndian(Entry(index)[4..]) << 32 | BinaryPrimitives.ReadUInt32BigEndian(Entry(index)[6..]);
        internal byte Type(uint index) => entries[(nuint)index * V5EntryBytes];
        internal uint Length(uint index)
        {
            ReadOnlySpan<byte> entry = Entry(index);
            return (uint)entry[1] << 16 | (uint)entry[2] << 8 | entry[3];
        }
        private ReadOnlySpan<byte> Entry(uint index) => new(entries + (nuint)index * V5EntryBytes, V5EntryBytes);
    }

    /// <summary>The on-disk v1–v4 map, decoded exactly as libchdr's map_extract and map_extract_old do.</summary>
    /// <remarks>The offset field is the data offset, the self-reference target or the MINI value, by entry kind.</remarks>
    private readonly struct LegacyMap(byte[] entries, int entryBytes, uint hunkBytes, uint count) : IHunkMap
    {
        public uint Count => count;
        public bool IsSelf(uint index) => Kind(index) == LegacySelf;
        public ulong Target(uint index) => entryBytes == 16 ? Raw(index) : Raw(index) & 0xFFFFFFFFFFFUL;
        internal uint Length(uint index) => entryBytes == 16
            ? BinaryPrimitives.ReadUInt16BigEndian(entries.AsSpan((int)index * 16 + 12, 2)) | (uint)entries[(int)index * 16 + 14] << 16
            : (uint)(Raw(index) >> 44);
        internal int Kind(uint index) => entryBytes == 16
            ? entries[(int)index * 16 + 15] & LegacyTypeMask
            : Raw(index) >> 44 == hunkBytes ? LegacyUncompressed : LegacyCompressed;
        private ulong Raw(uint index) => BinaryPrimitives.ReadUInt64BigEndian(entries.AsSpan((int)index * entryBytes, 8));
    }
}
