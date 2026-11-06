using System.Buffers.Binary;
using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Nintendo;

internal sealed record WiiIsoPartition(long Offset, long DataOffset, long DataBytes, uint Type, byte[] Ticket)
{
    public long Sectors => DataBytes / WiiRegion.SectorSize;
}
internal sealed record WiiRawExtent(long Offset, long Bytes)
{
    public long AlignedOffset => Offset - Offset % WiiRegion.SectorSize;
    public long End => Offset + Bytes;
}
internal abstract record WiiLayoutResult
{
    private protected WiiLayoutResult() { }
    internal sealed record Success(WiiIsoLayout Layout) : WiiLayoutResult;
    internal sealed record Failure(RvzEncodeResult.Failure Error) : WiiLayoutResult;
}

/// <summary>Bounded retail hashed/encrypted Wii profile; signature authenticity is not claimed.</summary>
internal sealed record WiiIsoLayout(long Length, byte[] DiscHeader, WiiIsoPartition[] Partitions, WiiRawExtent[] Raw)
{
    private const long DirectoryOffset = 0x40000;

    public static async Task<WiiLayoutResult> ReadAsync(IRandomAccessContent source, long logicalLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long length = source.Length;
        if (length > logicalLimit)
            return Fail(RvzEncodeFailureKind.LogicalLimitExceeded, "Disc exceeds the logical encoding limit.");
        if (length < DirectoryOffset + 32)
            return Invalid("Truncated Wii disc layout.");
        byte[] header = new byte[0x80], directory = new byte[32];
        if (!await WiiSourceRead.FillAsync(source, 0, header, ct))
            return Invalid("Invalid Wii disc header.");
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18)) != 0x5D1C9EA3)
            return Unsupported("Input is not a Wii disc.");
        if (header[0x60] > 1 || header[0x61] > 1)
            return Invalid("Invalid Wii hash/encryption flags.");
        if (header[0x60] != 0 || header[0x61] != 0)
            return Unsupported("This profile requires hashed encrypted Wii sectors.");
        if (!await WiiSourceRead.FillAsync(source, DirectoryOffset, directory, ct))
            return Invalid("Truncated Wii partition directory.");
        long count = 0;
        for (int group = 0; group < 4; group++)
            count += U32(directory, group * 8);
        if (count > 64)
            return Invalid("Wii partition count exceeds limits.");
        if (count == 0)
            return Unsupported("Wii discs without partitions are outside this profile.");
        var partitions = new List<WiiIsoPartition>((int)count);
        var tables = new List<(long Start, long End)>();
        byte[] entry = new byte[8], partHeader = new byte[0x2C0];
        for (int group = 0; group < 4; group++)
        {
            uint groupCount = U32(directory, group * 8);
            if (groupCount == 0)
                continue;
            long table = (long)U32(directory, group * 8 + 4) * 4, tableBytes = (long)groupCount * 8;
            if (table < DirectoryOffset + 32 || !Inside(table, tableBytes, length))
                return Invalid("Wii partition table is outside the disc.");
            if (tables.Any(other => table < other.End && other.Start < table + tableBytes))
                return Invalid("Wii partition tables overlap.");
            tables.Add((table, table + tableBytes));
            for (uint member = 0; member < groupCount; member++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await WiiSourceRead.FillAsync(source, table + member * 8L, entry, ct))
                    return Invalid("Truncated Wii partition table.");
                long offset = (long)U32(entry, 0) * 4;
                if (offset < DirectoryOffset + 32 || !Inside(offset, partHeader.Length, length))
                    return Invalid("Wii partition header is outside the disc.");
                if (!await WiiSourceRead.FillAsync(source, offset, partHeader, ct))
                    return Invalid("Truncated Wii partition header.");
                if (WiiTicket.Validate(partHeader.AsSpan(0, WiiTicket.Bytes)) is { } ticketError)
                    return new WiiLayoutResult.Failure(ticketError);
                long relativeData = (long)U32(partHeader, 0x2B8) * 4, dataBytes = (long)U32(partHeader, 0x2BC) * 4;
                long dataOffset = offset + relativeData;
                if (relativeData < partHeader.Length || dataBytes == 0 || dataOffset % WiiRegion.SectorSize != 0 ||
                    dataBytes % WiiRegion.SectorSize != 0 || !Inside(dataOffset, dataBytes, length))
                    return Invalid("Wii partition data extent is inconsistent.");
                var metadata = new List<(long Start, long End)> { ((long)U32(partHeader, 0x2B4) * 4, (long)U32(partHeader, 0x2B4) * 4 + 0x18000) };
                AddMetadata(metadata, partHeader, 0x2A4, 0x2A8);
                AddMetadata(metadata, partHeader, 0x2AC, 0x2B0);
                var ordered = metadata.OrderBy(extent => extent.Start).ToArray();
                for (int i = 0; i < ordered.Length; i++)
                    if (ordered[i].Start < partHeader.Length || ordered[i].End > relativeData ||
                        (i > 0 && ordered[i].Start < ordered[i - 1].End))
                        return Invalid("Wii partition metadata extents are inconsistent.");
                partitions.Add(new WiiIsoPartition(offset, dataOffset, dataBytes, U32(entry, 4), partHeader.AsSpan(0, WiiTicket.Bytes).ToArray()));
            }
        }
        var sorted = partitions.OrderBy(part => part.Offset).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            long end = sorted[i].DataOffset + sorted[i].DataBytes;
            if (i > 0 && sorted[i].Offset < sorted[i - 1].DataOffset + sorted[i - 1].DataBytes)
                return Invalid("Wii partitions overlap.");
            if (tables.Any(table => table.Start < end && sorted[i].Offset < table.End))
                return Invalid("Wii partition data overlaps its directory tables.");
        }
        var raw = new List<WiiRawExtent>(sorted.Length + 1);
        long cursor = 0x80;
        foreach (var partition in sorted)
        {
            if (partition.DataOffset > cursor)
                raw.Add(new WiiRawExtent(cursor, partition.DataOffset - cursor));
            cursor = partition.DataOffset + partition.DataBytes;
        }
        if (cursor < length)
            raw.Add(new WiiRawExtent(cursor, length - cursor));
        if (source.Length != length)
            return Invalid("Wii disc length changed during inspection.");
        return new WiiLayoutResult.Success(new WiiIsoLayout(length, header, sorted, raw.ToArray()));
    }

    private static void AddMetadata(List<(long Start, long End)> extents, byte[] header, int sizeAt, int offsetAt)
    {
        uint size = U32(header, sizeAt);
        if (size != 0)
        {
            long offset = (long)U32(header, offsetAt) * 4;
            extents.Add((offset, offset + size));
        }
    }
    private static bool Inside(long offset, long bytes, long length) => offset >= 0 && bytes >= 0 && offset <= length && bytes <= length - offset;
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
    private static WiiLayoutResult Invalid(string text) => Fail(RvzEncodeFailureKind.InvalidSource, text);
    private static WiiLayoutResult Unsupported(string text) => Fail(RvzEncodeFailureKind.UnsupportedMedia, text);
    private static WiiLayoutResult Fail(RvzEncodeFailureKind kind, string text) => new WiiLayoutResult.Failure(new RvzEncodeResult.Failure(kind, text));
}

internal static class WiiSourceRead
{
    public static async Task<bool> FillAsync(IRandomAccessContent source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        int position = 0;
        while (position < destination.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read;
            try
            { read = await source.ReadAtAsync(offset + position, destination[position..], ct); }
            catch (InvalidDataException) { return false; }
            ct.ThrowIfCancellationRequested();
            if (read <= 0 || read > destination.Length - position)
                return false;
            position += read;
        }
        return true;
    }
}
