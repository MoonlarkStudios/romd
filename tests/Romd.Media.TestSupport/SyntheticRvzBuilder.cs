using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using ZstdSharp;

namespace Romd.Media.TestSupport;

public enum RvzMethod : uint { None = 0, Bzip2 = 2, Lzma = 3, Lzma2 = 4, Zstd = 5 }

/// <summary>A seeded padding run. Raw runs use disc offsets; partition runs use hash-free partition data offsets.</summary>
public sealed record PaddingRun(long Offset, int Length, byte[] Seed);

public sealed record SyntheticRvz(byte[] File, byte[] ExpectedDisc);

/// <summary>
/// Writes small RVZ files from a logical disc model for tests, following Dolphin's
/// docs/WiaAndRvz.md. Expected disc bytes come from <see cref="ReferenceDisc"/>.
/// </summary>
public sealed class SyntheticRvzBuilder
{
    private const int SectorSize = ReferenceDisc.SectorSize, SectorData = ReferenceDisc.SectorData, WiiGroupSize = 0x200000;
    private sealed record PartitionSpec(long FirstSector, int Sectors, int FirstSegmentSectors, byte[] Key, byte[] Plain,
        IReadOnlyList<HashOverride> Overrides, IReadOnlyList<PaddingRun> Padding);

    private readonly byte[] _disc;
    private readonly int _chunkSize;
    private readonly RvzMethod _method;
    private readonly bool _wii;
    private readonly List<PaddingRun> _rawPadding = [];
    private readonly List<PartitionSpec> _partitions = [];
    private byte[] _lzmaProperties = [];

    public SyntheticRvzBuilder(long logicalSize, int chunkSize, RvzMethod method = RvzMethod.Zstd, bool wii = false, int seed = 1)
    {
        _disc = new byte[logicalSize];
        new Random(seed).NextBytes(_disc);
        _chunkSize = chunkSize;
        _method = method;
        _wii = wii;
        "RMDE01"u8.CopyTo(_disc);
        BinaryPrimitives.WriteUInt32BigEndian(_disc.AsSpan(0x18), wii ? 0x5D1C9EA3u : 0);
        BinaryPrimitives.WriteUInt32BigEndian(_disc.AsSpan(0x1C), wii ? 0u : 0xC2339F3D);
    }

    /// <summary>Stores selected group indices without compression (the data_size flag clear).</summary>
    public Func<int, bool> StoreUncompressed { get; init; } = _ => false;

    /// <summary>Encode all-zero groups without exceptions as stored size 0.</summary>
    public bool ZeroGroups { get; init; } = true;

    public int ZstdLevel { get; init; } = 3;

    public SyntheticRvzBuilder Fill(long offset, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_disc.AsSpan((int)offset));
        return this;
    }

    public SyntheticRvzBuilder Padding(long offset, int length, byte[] seed)
    {
        RequireWithinBlock(offset, length);
        ReferenceDisc.Padding(seed, (int)(offset % SectorSize), length).CopyTo(_disc, offset);
        _rawPadding.Add(new PaddingRun(offset, length, seed));
        return this;
    }

    /// <param name="plain">Hash-free partition data, <paramref name="sectors"/> × 0x7C00 bytes; padding runs are written into it.</param>
    public SyntheticRvzBuilder Partition(long firstSector, int sectors, byte[] key, byte[] plain, int firstSegmentSectors = 0,
        IReadOnlyList<HashOverride>? overrides = null, IReadOnlyList<PaddingRun>? padding = null)
    {
        if (plain.Length != (long)sectors * SectorData)
            throw new ArgumentException("Partition data must be sectors × 0x7C00 bytes.");
        foreach (var run in padding ?? [])
        {
            RequireWithinBlock(run.Offset, run.Length);
            ReferenceDisc.Padding(run.Seed, (int)(run.Offset % SectorSize), run.Length).CopyTo(plain, run.Offset);
        }
        _partitions.Add(new PartitionSpec(firstSector, sectors, firstSegmentSectors, key, plain, overrides ?? [], padding ?? []));
        return this;
    }

    public SyntheticRvz Build()
    {
        foreach (var partition in _partitions)
            ReferenceDisc.EncryptPartition(partition.Key, partition.Plain, partition.Sectors, partition.Overrides)
                .CopyTo(_disc, partition.FirstSector * SectorSize);

        var groups = new List<byte[]>();
        var groupEntries = new List<(bool Compressed, int PackedSize)>();
        var partitionTable = new MemoryStream();
        foreach (var partition in _partitions.OrderBy(item => item.FirstSector))
        {
            partitionTable.Write(partition.Key);
            int first = partition.FirstSegmentSectors;
            var segments = first > 0
                ? new[] { (Start: 0, Sectors: first), (Start: first, Sectors: partition.Sectors - first) }
                : new[] { (Start: 0, Sectors: partition.Sectors), (Start: 0, Sectors: 0) };
            foreach (var segment in segments)
            {
                int firstGroup = groups.Count;
                if (segment.Sectors > 0)
                    AddPartitionGroups(partition, segment.Start, segment.Sectors, groups, groupEntries);
                WriteBe32(partitionTable, segment.Sectors == 0 ? 0u : (uint)(partition.FirstSector + segment.Start));
                WriteBe32(partitionTable, (uint)segment.Sectors);
                WriteBe32(partitionTable, (uint)firstGroup);
                WriteBe32(partitionTable, (uint)(groups.Count - firstGroup));
            }
        }

        var rawTable = new MemoryStream();
        var gaps = RawAreas();
        foreach (var (offset, size) in gaps)
        {
            int firstGroup = groups.Count;
            long start = offset - offset % SectorSize;
            for (long chunk = start; chunk < offset + size; chunk += _chunkSize)
            {
                int length = (int)Math.Min(_chunkSize, offset + size - chunk);
                var runs = _rawPadding.Where(run => run.Offset >= chunk && run.Offset < chunk + length).ToList();
                AddGroup(Array.Empty<byte>(), _disc.AsSpan((int)chunk, length).ToArray(), runs, chunk, groups, groupEntries);
            }
            WriteBe64(rawTable, (ulong)offset);
            WriteBe64(rawTable, (ulong)size);
            WriteBe32(rawTable, (uint)firstGroup);
            WriteBe32(rawTable, (uint)(groups.Count - firstGroup));
        }

        const int headerBytes = 0x48 + 0xDC;
        var file = new MemoryStream();
        file.Write(new byte[headerBytes]);
        file.Write(partitionTable.ToArray());
        var groupTable = new MemoryStream();
        for (int i = 0; i < groups.Count; i++)
        {
            byte[] data = groups[i];
            var (compressed, packed) = groupEntries[i];
            while (file.Length % 4 != 0)
                file.WriteByte(0);
            long position = file.Length;
            byte[] stored = data.Length == 0 ? data : compressed ? CompressData(data) : data;
            file.Write(stored);
            WriteBe32(groupTable, stored.Length == 0 ? 0u : (uint)(position / 4));
            WriteBe32(groupTable, (uint)stored.Length | (compressed && stored.Length > 0 ? 0x80000000u : 0));
            WriteBe32(groupTable, (uint)packed);
        }
        byte[] rawStored = _method == RvzMethod.None ? rawTable.ToArray() : CompressData(rawTable.ToArray());
        byte[] groupStored = _method == RvzMethod.None ? groupTable.ToArray() : CompressData(groupTable.ToArray());
        long rawOffset = file.Length;
        file.Write(rawStored);
        long groupOffset = file.Length;
        file.Write(groupStored);
        byte[] bytes = file.ToArray();

        var disc = bytes.AsSpan(0x48, 0xDC);
        BinaryPrimitives.WriteUInt32BigEndian(disc, _wii ? 2u : 1u);
        BinaryPrimitives.WriteUInt32BigEndian(disc[4..], (uint)_method);
        BinaryPrimitives.WriteInt32BigEndian(disc[8..], _method == RvzMethod.Zstd ? ZstdLevel : 6);
        BinaryPrimitives.WriteUInt32BigEndian(disc[12..], (uint)_chunkSize);
        _disc.AsSpan(0, 0x80).CopyTo(disc[16..]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[144..], (uint)_partitions.Count);
        BinaryPrimitives.WriteUInt32BigEndian(disc[148..], 0x30);
        BinaryPrimitives.WriteUInt64BigEndian(disc[152..], headerBytes);
        SHA1.HashData(partitionTable.ToArray()).CopyTo(disc[160..]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[180..], (uint)gaps.Count);
        BinaryPrimitives.WriteUInt64BigEndian(disc[184..], (ulong)rawOffset);
        BinaryPrimitives.WriteUInt32BigEndian(disc[192..], (uint)rawStored.Length);
        BinaryPrimitives.WriteUInt32BigEndian(disc[196..], (uint)groups.Count);
        BinaryPrimitives.WriteUInt64BigEndian(disc[200..], (ulong)groupOffset);
        BinaryPrimitives.WriteUInt32BigEndian(disc[208..], (uint)groupStored.Length);
        byte[] properties = _method switch { RvzMethod.Lzma => _lzmaProperties, RvzMethod.Lzma2 => [0x18], _ => [] };
        disc[212] = (byte)properties.Length;
        properties.CopyTo(disc[213..]);

        var head = bytes.AsSpan(0, 0x48);
        "RVZ\u0001"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], 0x01000000);
        BinaryPrimitives.WriteUInt32BigEndian(head[8..], 0x00030000);
        BinaryPrimitives.WriteUInt32BigEndian(head[12..], 0xDC);
        SHA1.HashData(bytes.AsSpan(0x48, 0xDC)).CopyTo(head[16..]);
        BinaryPrimitives.WriteUInt64BigEndian(head[0x24..], (ulong)_disc.Length);
        BinaryPrimitives.WriteUInt64BigEndian(head[0x2C..], (ulong)bytes.Length);
        SHA1.HashData(head[..0x34]).CopyTo(head[0x34..]);
        return new SyntheticRvz(bytes, _disc.ToArray());
    }

    private void AddPartitionGroups(PartitionSpec partition, int startSector, int sectors,
        List<byte[]> groups, List<(bool, int)> entries)
    {
        int sectorsPerChunk = _chunkSize / SectorSize, lists = Math.Max(1, _chunkSize / WiiGroupSize);
        for (int chunk = startSector; chunk < startSector + sectors; chunk += sectorsPerChunk)
        {
            int chunkSectors = Math.Min(sectorsPerChunk, startSector + sectors - chunk);
            var exceptionLists = new MemoryStream();
            for (int list = 0; list < lists; list++)
            {
                var items = partition.Overrides
                    .Where(item => item.Sector >= chunk + list * 64 && item.Sector < Math.Min(chunk + chunkSectors, chunk + (list + 1) * 64))
                    .ToList();
                WriteBe16(exceptionLists, (ushort)items.Count);
                foreach (var item in items)
                {
                    WriteBe16(exceptionLists, (ushort)(((item.Sector - chunk - list * 64) << 10) | item.Offset));
                    exceptionLists.Write(item.Hash);
                }
            }
            long dataOffset = (long)chunk * SectorData;
            int length = chunkSectors * SectorData;
            var runs = partition.Padding.Where(run => run.Offset >= dataOffset && run.Offset < dataOffset + length).ToList();
            AddGroup(exceptionLists.ToArray(), partition.Plain.AsSpan((int)dataOffset, length).ToArray(), runs, dataOffset,
                groups, entries, hasExceptions: partition.Overrides.Any(item => item.Sector >= chunk && item.Sector < chunk + chunkSectors));
        }
    }

    private void AddGroup(byte[] exceptionLists, byte[] payload, List<PaddingRun> runs, long dataOffset,
        List<byte[]> groups, List<(bool, int)> entries, bool hasExceptions = false)
    {
        int index = groups.Count;
        if (ZeroGroups && !hasExceptions && runs.Count == 0 && payload.All(value => value == 0))
        {
            groups.Add([]);
            entries.Add((false, 0));
            return;
        }
        byte[] body = runs.Count == 0 ? payload : Pack(payload, runs, dataOffset);
        bool compressed = _method != RvzMethod.None && !StoreUncompressed(index);
        var data = new MemoryStream();
        data.Write(exceptionLists);
        if (!compressed && exceptionLists.Length > 0)
            while (data.Length % 4 != 0)
                data.WriteByte(0);
        data.Write(body);
        groups.Add(data.ToArray());
        entries.Add((compressed, runs.Count == 0 ? 0 : body.Length));
    }

    private static byte[] Pack(byte[] payload, List<PaddingRun> runs, long dataOffset)
    {
        var packed = new MemoryStream();
        int position = 0;
        foreach (var run in runs.OrderBy(item => item.Offset))
        {
            int start = (int)(run.Offset - dataOffset);
            if (start > position)
            {
                WriteBe32(packed, (uint)(start - position));
                packed.Write(payload, position, start - position);
            }
            WriteBe32(packed, 0x80000000u | (uint)run.Length);
            packed.Write(run.Seed);
            position = start + run.Length;
        }
        if (position < payload.Length)
        {
            WriteBe32(packed, (uint)(payload.Length - position));
            packed.Write(payload, position, payload.Length - position);
        }
        return packed.ToArray();
    }

    private List<(long Offset, long Size)> RawAreas()
    {
        var areas = new List<(long, long)>();
        long cursor = 0x80;
        foreach (var partition in _partitions.OrderBy(item => item.FirstSector))
        {
            long start = partition.FirstSector * SectorSize;
            if (start > cursor)
                areas.Add((cursor, start - cursor));
            cursor = start + (long)partition.Sectors * SectorSize;
        }
        if (_disc.Length > cursor)
            areas.Add((cursor, _disc.Length - cursor));
        return areas;
    }

    private byte[] CompressData(byte[] data)
    {
        switch (_method)
        {
            case RvzMethod.Zstd:
                using (var compressor = new Compressor(ZstdLevel))
                    return compressor.Wrap(data).ToArray();
            case RvzMethod.Bzip2:
                {
                    var output = new MemoryStream();
                    using (var stream = BZip2Stream.Create(output, CompressionMode.Compress, false, leaveOpen: true, tolerateTruncatedStream: false))
                        stream.Write(data);
                    return output.ToArray();
                }
            case RvzMethod.Lzma:
                {
                    var output = new MemoryStream();
                    using (var stream = LzmaStream.Create(new LzmaEncoderProperties(eos: true, dictionary: 1 << 22), false, output))
                    {
                        _lzmaProperties = stream.Properties!;
                        stream.Write(data);
                    }
                    return output.ToArray();
                }
            case RvzMethod.Lzma2:
                {
                    // SharpCompress cannot encode LZMA2; uncompressed LZMA2 chunks are still a valid stream.
                    var output = new MemoryStream();
                    for (int offset = 0; offset < data.Length; offset += 0x10000)
                    {
                        int length = Math.Min(0x10000, data.Length - offset);
                        output.WriteByte(offset == 0 ? (byte)0x01 : (byte)0x02);
                        WriteBe16(output, (ushort)(length - 1));
                        output.Write(data, offset, length);
                    }
                    output.WriteByte(0x00);
                    return output.ToArray();
                }
            default:
                return data;
        }
    }

    private static void RequireWithinBlock(long offset, int length)
    {
        if (length <= 0 || offset / SectorSize != (offset + length - 1) / SectorSize)
            throw new ArgumentException("Padding runs must stay within one 32 KiB block.");
    }

    private static void WriteBe16(Stream stream, ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private static void WriteBe32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    private static void WriteBe64(Stream stream, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); }
}
