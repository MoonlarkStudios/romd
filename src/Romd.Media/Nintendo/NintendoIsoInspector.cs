using System.Buffers.Binary;
using Romd.Storage;

namespace Romd.Media.Nintendo;

/// <summary>
/// Inspects bounded GameCube boot/FST extents and hashed, encrypted Wii partition layouts.
/// Does not decrypt tickets, authenticate signatures, hash the source or take ownership of it.
/// </summary>
public static class NintendoIsoInspector
{
    public const string ProfileVersion = "nintendo-iso/v1";
    private const uint GameCubeMagic = 0xC2339F3D, WiiMagic = 0x5D1C9EA3;
    private const int MaximumFstEntries = 100_000, MaximumFstBytes = 16 << 20, MaximumPartitions = 64;
    private const int GameCubeBootBytes = 0x2440, ApploaderHeaderBytes = 0x20, DolHeaderBytes = 0x100;
    private const int WiiHeaderBytes = 0x2C0, WiiSectorBytes = 0x8000;
    private const long WiiDirectoryOffset = 0x40000;

    public static async Task<NintendoIsoInspectionResult> InspectAsync(IRandomAccessContent source,
        long maxLogicalBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLogicalBytes);
        ct.ThrowIfCancellationRequested();
        long length = source.Length;
        if (length > maxLogicalBytes)
            return LimitExceeded();
        if (length < 0x20)
            return Malformed();
        byte[] header = new byte[0x80];
        if (length < header.Length || !await FillAsync(source, 0, header, ct))
            return Malformed();
        bool gameCube = U32(header, 0x1C) == GameCubeMagic, wii = U32(header, 0x18) == WiiMagic;
        if (gameCube && wii)
            return Malformed();
        if (!gameCube && !wii)
            return Unsupported();
        var result = gameCube
            ? await InspectGameCubeAsync(source, length, ct)
            : await InspectWiiAsync(source, length, header, ct);
        ct.ThrowIfCancellationRequested();
        return source.Length == length ? result : Malformed();
    }

    private static async Task<NintendoIsoInspectionResult> InspectGameCubeAsync(IRandomAccessContent source,
        long length, CancellationToken ct)
    {
        if (length < GameCubeBootBytes + ApploaderHeaderBytes)
            return Malformed();
        byte[] boot = new byte[0x440], apploader = new byte[ApploaderHeaderBytes], dol = new byte[DolHeaderBytes];
        if (!await FillAsync(source, 0, boot, ct) || !await FillAsync(source, GameCubeBootBytes, apploader, ct))
            return Malformed();
        long apploaderBytes = ApploaderHeaderBytes + (long)U32(apploader, 0x14) + U32(apploader, 0x18);
        var bootExtent = new Extent(0, GameCubeBootBytes + apploaderBytes);
        if (!Inside(bootExtent, length))
            return Malformed();
        long dolOffset = U32(boot, 0x420), fstOffset = U32(boot, 0x424), fstBytes = U32(boot, 0x428);
        var dolHeader = new Extent(dolOffset, DolHeaderBytes);
        var fstExtent = new Extent(fstOffset, fstBytes);
        if (fstBytes < 12 || !Inside(dolHeader, length) || !Inside(fstExtent, length) ||
            Overlap(bootExtent, dolHeader) || Overlap(bootExtent, fstExtent) || Overlap(dolHeader, fstExtent))
            return Malformed();
        if (fstBytes > MaximumFstBytes)
            return Unsupported();
        if (!await FillAsync(source, dolOffset, dol, ct))
            return Malformed();
        for (int section = 0; section < 18; section++)
        {
            long bytes = U32(dol, 0x90 + section * 4);
            if (bytes == 0)
                continue;
            long relative = U32(dol, section * 4);
            var extent = new Extent(dolOffset + relative, bytes);
            if (relative < DolHeaderBytes || !Inside(extent, length) || Overlap(bootExtent, extent) || Overlap(fstExtent, extent))
                return Malformed();
        }
        byte[] root = new byte[12];
        if (!await FillAsync(source, fstOffset, root, ct))
            return Malformed();
        uint entries = U32(root, 8);
        if (root[0] != 1 || U32(root, 4) != 0 || entries == 0 || (long)entries * 12 > fstBytes)
            return Malformed();
        if (entries > MaximumFstEntries)
            return Unsupported();
        byte[] fst = new byte[(int)fstBytes];
        if (!await FillAsync(source, fstOffset, fst, ct))
            return Malformed();
        int stringOffset = (int)entries * 12;
        int lastNameTerminator = fst.AsSpan(stringOffset).LastIndexOf((byte)0);
        var parents = new Stack<(uint Index, uint End)>();
        parents.Push((0, entries));
        for (uint index = 1; index < entries; index++)
        {
            ct.ThrowIfCancellationRequested();
            while (parents.Count > 0 && index >= parents.Peek().End)
                parents.Pop();
            int at = (int)index * 12;
            uint name = U32(fst, at) & 0xFFFFFF;
            if (parents.Count == 0 || name > lastNameTerminator)
                return Malformed();
            if (fst[at] == 0)
            {
                // File aliases are allowed: each declared extent must fit independently.
                if (!Inside(new Extent(U32(fst, at + 4), U32(fst, at + 8)), length))
                    return Malformed();
            }
            else if (fst[at] == 1)
            {
                uint parent = U32(fst, at + 4), end = U32(fst, at + 8);
                if (parent != parents.Peek().Index || end <= index || end > parents.Peek().End)
                    return Malformed();
                parents.Push((index, end));
            }
            else
                return Malformed();
        }
        return Eligible(NintendoDiscKind.GameCube, length, 0, 0, 0);
    }

    private static async Task<NintendoIsoInspectionResult> InspectWiiAsync(IRandomAccessContent source,
        long length, byte[] header, CancellationToken ct)
    {
        // Extent arithmetic follows the independently reviewed Gate8 WiiIsoLayout precursor;
        // ticket handling here only checks signature layout and the common-key index.
        if (header[0x60] > 1 || header[0x61] > 1)
            return Malformed();
        if (header[0x60] != 0 || header[0x61] != 0)
            return Unsupported();
        if (!Inside(new Extent(WiiDirectoryOffset, 32), length))
            return Malformed();
        byte[] directory = new byte[32], entry = new byte[8], partitionHeader = new byte[WiiHeaderBytes];
        if (!await FillAsync(source, WiiDirectoryOffset, directory, ct))
            return Malformed();
        long count = 0;
        for (int group = 0; group < 4; group++)
            count += U32(directory, group * 8);
        if (count > MaximumPartitions)
            return Unsupported();
        if (count == 0)
            return Unsupported();
        var tables = new List<Extent>(4);
        for (int group = 0; group < 4; group++)
        {
            uint groupCount = U32(directory, group * 8);
            if (groupCount == 0)
                continue;
            var table = new Extent((long)U32(directory, group * 8 + 4) * 4, (long)groupCount * 8);
            if (table.Offset < WiiDirectoryOffset + 32 || !Inside(table, length) || tables.Any(other => Overlap(table, other)))
                return Malformed();
            tables.Add(table);
        }
        var partitions = new List<Extent>((int)count);
        int standard = 0, korean = 0;
        for (int group = 0; group < 4; group++)
        {
            uint groupCount = U32(directory, group * 8);
            long tableOffset = (long)U32(directory, group * 8 + 4) * 4;
            for (uint member = 0; member < groupCount; member++)
            {
                if (!await FillAsync(source, tableOffset + member * 8L, entry, ct))
                    return Malformed();
                long offset = (long)U32(entry, 0) * 4;
                var partitionHeaderExtent = new Extent(offset, WiiHeaderBytes);
                if (offset < WiiDirectoryOffset + 32 || !Inside(partitionHeaderExtent, length) ||
                    tables.Any(table => Overlap(partitionHeaderExtent, table)))
                    return Malformed();
                if (!await FillAsync(source, offset, partitionHeader, ct))
                    return Malformed();
                if (U32(partitionHeader, 0) != 0x00010001 || partitionHeader[0x1F1] > 1)
                    return Unsupported();
                long relativeData = (long)U32(partitionHeader, 0x2B8) * 4, dataBytes = (long)U32(partitionHeader, 0x2BC) * 4;
                long dataOffset = offset + relativeData;
                var partition = new Extent(offset, relativeData + dataBytes);
                if (relativeData < WiiHeaderBytes || dataBytes == 0 || dataOffset % WiiSectorBytes != 0 ||
                    dataBytes % WiiSectorBytes != 0 || !Inside(partition, length) || tables.Any(table => Overlap(partition, table)) ||
                    partitions.Any(other => Overlap(partition, other)))
                    return Malformed();
                Extent[] metadata = [
                    new((long)U32(partitionHeader, 0x2A8) * 4, U32(partitionHeader, 0x2A4)),
                    new((long)U32(partitionHeader, 0x2B0) * 4, U32(partitionHeader, 0x2AC)),
                    new((long)U32(partitionHeader, 0x2B4) * 4, 0x18000)
                ];
                for (int section = 0; section < metadata.Length; section++)
                {
                    var extent = metadata[section];
                    if (extent.Bytes == 0)
                        continue;
                    if (extent.Offset < WiiHeaderBytes || !Inside(extent, relativeData))
                        return Malformed();
                    for (int prior = 0; prior < section; prior++)
                        if (Overlap(extent, metadata[prior]))
                            return Malformed();
                }
                partitions.Add(partition);
                if (partitionHeader[0x1F1] == 0)
                    standard++;
                else
                    korean++;
            }
        }
        return Eligible(NintendoDiscKind.Wii, length, (int)count, standard, korean);
    }

    private static async Task<bool> FillAsync(IRandomAccessContent source, long offset, Memory<byte> destination,
        CancellationToken ct)
    {
        for (int position = 0; position < destination.Length;)
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

    private readonly record struct Extent(long Offset, long Bytes);
    private static bool Inside(Extent extent, long length) => extent.Offset >= 0 && extent.Bytes >= 0 &&
        extent.Offset <= length && extent.Bytes <= length - extent.Offset;
    private static bool Overlap(Extent left, Extent right) => left.Bytes != 0 && right.Bytes != 0 &&
        left.Offset < right.Offset + right.Bytes && right.Offset < left.Offset + left.Bytes;
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
    private static NintendoIsoInspectionResult Eligible(NintendoDiscKind kind, long length, int partitions, int standard, int korean) =>
        new(NintendoIsoInspectionStatus.Eligible, new(kind, length, partitions, standard, korean));
    private static NintendoIsoInspectionResult Unsupported() => new(NintendoIsoInspectionStatus.Unsupported);
    private static NintendoIsoInspectionResult Malformed() => new(NintendoIsoInspectionStatus.Malformed);
    private static NintendoIsoInspectionResult LimitExceeded() => new(NintendoIsoInspectionStatus.LimitExceeded);
}
