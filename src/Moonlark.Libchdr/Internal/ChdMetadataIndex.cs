using System.Buffers.Binary;

namespace Moonlark.Libchdr.Internal;

internal readonly record struct MetadataEntry(ChdMetadataInfo Info, long PayloadOffset);

internal static class ChdMetadataIndex
{
    internal static MetadataEntry[] Read(ChdSourceContext source, ulong first, uint headerLength)
    {
        if (first == 0)
            return [];
        var entries = new List<MetadataEntry>();
        var visited = new HashSet<ulong>();
        Span<byte> header = stackalloc byte[16];
        long length = source.SourceLength;
        ulong offset = first;
        while (offset != 0)
        {
            if (entries.Count == 65536 || !visited.Add(offset) || offset < headerLength ||
                offset > (ulong)length || (ulong)length - offset < 16)
                throw new ChdValidationException(ChdError.InvalidMetadata, $"metadata chain at offset {offset}");
            ReadExactly(source, (long)offset, header);
            uint packed = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            uint payloadLength = packed & 0x00ffffff;
            long payloadOffset = checked((long)offset + 16);
            if (payloadLength > length - payloadOffset)
                throw new ChdValidationException(ChdError.InvalidMetadataSize, $"metadata payload at offset {payloadOffset}");
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(header);
            entries.Add(new(new(new(tag), payloadLength, (byte)(packed >> 24)), payloadOffset));
            offset = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        }
        return entries.ToArray();
    }

    internal static void ReadExactly(ChdSourceContext source, long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = source.ReadSourceAt(checked(offset + total), destination[total..]);
            if (read == 0)
                throw new ChdValidationException(ChdError.ReadError, $"source read at offset {offset + total}");
            total += read;
        }
    }
}
