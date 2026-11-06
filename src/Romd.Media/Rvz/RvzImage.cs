using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Media.Nintendo;
using Romd.Storage;

namespace Romd.Media.Rvz;

/// <summary>
/// The original disc bytes reconstructed from an RVZ artifact (Dolphin's docs/WiaAndRvz.md).
/// Thread-safe positional reads; decoded chunks and Wii regions go through a shared block cache,
/// which shares one decode among concurrent readers, and every working buffer comes from that
/// cache's bounded pool. Owns the artifact.
/// </summary>
public sealed class RvzImage : IRandomAccessContent
{
    private const int HeadSize = 0x48, DiscHeaderSize = 0x80, MinDiscStructSize = 0xDC;
    private const int SectorSize = WiiRegion.SectorSize, SectorData = WiiRegion.SectorData, WiiGroupSize = 0x200000;
    private const int RegionSize = WiiRegion.Sectors * SectorSize, MaxExceptions = 3328, ExceptionBytes = 2 + WiiRegion.HashSize;
    private const int CachedExceptionBytes = 4 + WiiRegion.HashSize;
    private const uint SupportedVersion = 0x01000000, OldestReadableVersion = 0x00030000;

    private sealed record Area(int Id, long Start, long End, int Partition, uint FirstGroup, uint GroupCount, long Sectors);
    private readonly record struct Group(long Offset, int StoredSize, bool Compressed, int PackedSize);

    private readonly IRandomAccessContent _artifact;
    private readonly RvzReadOptions _options;
    private readonly MediaBlockCache _cache;
    private readonly string _owner;
    private readonly bool _privateOwner;
    private readonly RvzCodec? _codec;
    private readonly byte[] _discHeader;
    private readonly Area[] _areas;
    private readonly Group[] _groups;
    private readonly byte[][] _keys;
    private readonly TaskCompletionSource _decodesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _decodes = 1; // Owned decodes in flight, plus one until disposal begins.
    private int _disposed;

    private RvzImage(IRandomAccessContent artifact, RvzReadOptions options, MediaBlockCache cache, string? cacheIdentity,
        RvzCodec? codec, RvzDescriptor descriptor, long length, byte[] discHeader, Area[] areas, Group[] groups, byte[][] keys)
    {
        _artifact = artifact;
        _options = options;
        _cache = cache;
        _privateOwner = cacheIdentity is null;
        _owner = cacheIdentity ?? Guid.NewGuid().ToString("N");
        _codec = codec;
        Descriptor = descriptor;
        Length = length;
        _discHeader = discHeader;
        _areas = areas;
        _groups = groups;
        _keys = keys;
    }

    public long Length { get; }
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
    public RvzDescriptor Descriptor { get; }

    /// <summary>
    /// Validates the container and its tables. Takes ownership of <paramref name="artifact"/>, including on failure.
    /// </summary>
    /// <param name="cacheIdentity">Identity of the artifact bytes (for example its SHA-256), letting images of the
    /// same artifact share cached blocks. Null keeps this image's blocks private and evicts them on disposal.</param>
    public static async Task<RvzImage> OpenAsync(IRandomAccessContent artifact, RvzReadOptions options,
        MediaBlockCache cache, string? cacheIdentity = null, CancellationToken ct = default)
    {
        RvzCodec? codec = null;
        try
        {
            options.Validated();
            ct.ThrowIfCancellationRequested();
            byte[] head = new byte[HeadSize];
            await ReadArtifactAsync(artifact, 0, head, ct);
            if (!head.AsSpan(0, 4).SequenceEqual("RVZ\u0001"u8))
                throw new InvalidDataException("Not an RVZ file.");
            Verify(head.AsSpan(0, 0x34), head.AsSpan(0x34, 20), "file header");
            uint version = Be32(head, 4), compatible = Be32(head, 8), discSize = Be32(head, 12);
            if (compatible > SupportedVersion || version < OldestReadableVersion)
                throw new NotSupportedException("This RVZ version is not supported.");
            long logicalSize = checked((long)Be64(head, 0x24));
            if (checked((long)Be64(head, 0x2C)) != artifact.Length)
                throw new InvalidDataException("RVZ file size mismatch.");
            if (discSize is < MinDiscStructSize or > 0x1000)
                throw new InvalidDataException("Unsupported RVZ disc struct size.");
            if (logicalSize <= DiscHeaderSize || logicalSize > options.MaxLogicalBytes)
                throw new InvalidDataException("The RVZ logical size is outside limits.");

            byte[] disc = new byte[discSize];
            await ReadArtifactAsync(artifact, HeadSize, disc, ct);
            Verify(disc, head.AsSpan(0x10, 20), "disc struct");
            uint compression = Be32(disc, 4), chunk = Be32(disc, 12);
            int propertyLength = disc[212];
            if (propertyLength > 7)
                throw new InvalidDataException("Invalid RVZ compression properties.");
            codec = RvzCodec.Create(compression, disc.AsSpan(213, propertyLength));
            if (chunk < 0x8000 || chunk > options.MaxChunkBytes ||
                (chunk < WiiGroupSize ? (chunk & (chunk - 1)) != 0 : chunk % WiiGroupSize != 0))
                throw new InvalidDataException("Invalid RVZ chunk size.");
            int chunkSize = (int)chunk;

            uint partitionCount = Be32(disc, 144), partitionEntrySize = Be32(disc, 148);
            uint rawCount = Be32(disc, 180), rawTableSize = Be32(disc, 192), groupCount = Be32(disc, 196), groupTableSize = Be32(disc, 208);
            long partitionOffset = checked((long)Be64(disc, 152)), rawOffset = checked((long)Be64(disc, 184)), groupOffset = checked((long)Be64(disc, 200));
            long maxGroups = logicalSize / 0x7C00 + 1 + rawCount + 2L * partitionCount;
            if (partitionCount > 64 || partitionEntrySize is < 16 or > 0x1000 || rawCount > 4096 ||
                groupCount > maxGroups || rawTableSize > 1u << 20 || groupTableSize > 64u << 20)
                throw new InvalidDataException("RVZ tables exceed limits.");

            byte[] partitionTable = new byte[partitionCount * partitionEntrySize];
            await ReadArtifactAsync(artifact, partitionOffset, partitionTable, ct);
            Verify(partitionTable, disc.AsSpan(160, 20), "partition table");
            byte[] rawTable = await ReadTableAsync(artifact, codec, rawOffset, (int)rawTableSize, checked((int)rawCount * 24), ct);
            byte[] groupTable = await ReadTableAsync(artifact, codec, groupOffset, (int)groupTableSize, checked((int)groupCount * 12), ct);

            int maxStored = MaxDecodedBytes(chunkSize);
            var groups = new Group[groupCount];
            for (int i = 0; i < groups.Length; i++)
            {
                long offset = (long)Be32(groupTable, i * 12) * 4;
                uint size = Be32(groupTable, i * 12 + 4), packed = Be32(groupTable, i * 12 + 8);
                int stored = (int)(size & 0x7FFFFFFF);
                if (stored > maxStored || packed > maxStored || offset + stored > artifact.Length)
                    throw new InvalidDataException("An RVZ group entry is outside limits.");
                groups[i] = new Group(offset, stored, (size & 0x80000000) != 0, (int)packed);
            }

            var areas = new List<Area>();
            var keys = new byte[partitionCount][];
            int sectorsPerChunk = chunkSize / SectorSize;
            byte[] entry = new byte[Math.Max(48, (int)partitionEntrySize)];
            for (int p = 0; p < partitionCount; p++)
            {
                Array.Clear(entry);
                partitionTable.AsSpan(p * (int)partitionEntrySize, (int)partitionEntrySize).CopyTo(entry);
                keys[p] = entry.AsSpan(0, 16).ToArray();
                for (int segment = 0; segment < 2; segment++)
                {
                    int at = 16 + segment * 16;
                    uint firstSector = Be32(entry, at), sectors = Be32(entry, at + 4), firstGroup = Be32(entry, at + 8), count = Be32(entry, at + 12);
                    if (sectors == 0)
                        continue;
                    if (count != (sectors + (uint)sectorsPerChunk - 1) / (uint)sectorsPerChunk || (ulong)firstGroup + count > groupCount)
                        throw new InvalidDataException("An RVZ partition group range is inconsistent.");
                    long start = (long)firstSector * SectorSize;
                    areas.Add(new Area(areas.Count, start, start + (long)sectors * SectorSize, p, firstGroup, count, sectors));
                }
            }
            for (int r = 0; r < rawCount; r++)
            {
                long offset = checked((long)Be64(rawTable, r * 24)), size = checked((long)Be64(rawTable, r * 24 + 8));
                uint firstGroup = Be32(rawTable, r * 24 + 16), count = Be32(rawTable, r * 24 + 20);
                if (size == 0)
                    continue;
                // The stored data starts at the preceding 32 KiB boundary (the first entry starts at 0x80).
                long start = offset - offset % SectorSize, end = checked(offset + size);
                if (count != (end - start + chunkSize - 1) / chunkSize || (ulong)firstGroup + count > groupCount)
                    throw new InvalidDataException("An RVZ raw data group range is inconsistent.");
                areas.Add(new Area(areas.Count, start, end, -1, firstGroup, count, 0));
            }
            var sorted = areas.OrderBy(area => area.Start).ToArray();
            for (int i = 0; i < sorted.Length; i++)
                if (sorted[i].End > logicalSize || (i > 0 && sorted[i].Start < sorted[i - 1].End))
                    throw new InvalidDataException("RVZ data areas overlap or exceed the disc.");

            var descriptor = new RvzDescriptor(version, Be32(disc, 0), compression, unchecked((int)Be32(disc, 8)), chunkSize, partitionCount > 0);
            return new RvzImage(artifact, options, cache, cacheIdentity, codec, descriptor, logicalSize,
                disc.AsSpan(16, DiscHeaderSize).ToArray(), sorted, groups, keys);
        }
        catch
        {
            codec?.Dispose();
            await artifact.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (offset >= Length || destination.IsEmpty)
            return 0;
        var remaining = destination[..(int)Math.Min(destination.Length, Length - offset)];
        long position = offset;
        int total = 0;
        if (position < DiscHeaderSize)
        {
            int take = (int)Math.Min(remaining.Length, DiscHeaderSize - position);
            _discHeader.AsMemory((int)position, take).CopyTo(remaining);
            position += take;
            total += take;
            remaining = remaining[take..];
        }
        while (!remaining.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            var area = FindArea(position) ?? throw new InvalidDataException($"No RVZ data covers disc offset 0x{position:X}.");
            long unit = area.Partition >= 0 ? RegionSize : Descriptor.ChunkSize;
            long index = (position - area.Start) / unit, unitStart = area.Start + index * unit;
            var block = await GetBlockAsync(area, index, ct);
            try
            {
                int inBlock = (int)(position - unitStart);
                int take = Math.Min(remaining.Length, block.Length - inBlock);
                if (take <= 0)
                    throw new InvalidDataException("An RVZ block is shorter than its data area.");
                block.Buffer.AsMemory(inBlock, take).CopyTo(remaining);
                position += take;
                total += take;
                remaining = remaining[take..];
            }
            finally { _cache.Release(block); }
        }
        return total;
    }

    private Area? FindArea(long position)
    {
        int low = 0, high = _areas.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            var area = _areas[middle];
            if (position < area.Start)
                high = middle - 1;
            else if (position >= area.End)
                low = middle + 1;
            else
                return area;
        }
        return null;
    }

    private ValueTask<Block> GetBlockAsync(Area area, long index, CancellationToken ct) =>
        _cache.GetOrAddAsync(new BlockKey(_owner, area.Id, index), (Image: this, Area: area, Index: index),
            static state => state.Image.DecodeOwnedAsync(state.Area, state.Index), ct);

    /// <summary>
    /// A decode other readers, including images of the same identity, may share; no reader's token cancels it.
    /// Disposal waits for it before releasing the artifact and codec.
    /// </summary>
    private async Task<Block> DecodeOwnedAsync(Area area, long index)
    {
        ObjectDisposedException.ThrowIf(!TryBeginDecode(), this);
        try
        { return area.Partition >= 0 ? await DecodeRegionAsync(area, index) : await DecodeRawChunkAsync(area, index); }
        finally { EndDecode(); }
    }

    private bool TryBeginDecode()
    {
        for (int current = Volatile.Read(ref _decodes); current > 0;)
        {
            int seen = Interlocked.CompareExchange(ref _decodes, current + 1, current);
            if (seen == current)
                return true;
            current = seen;
        }
        return false;
    }

    private void EndDecode()
    {
        if (Interlocked.Decrement(ref _decodes) == 0)
            _decodesDrained.TrySetResult();
    }

    private async ValueTask<Block> DecodeRawChunkAsync(Area area, long index)
    {
        int chunkSize = Descriptor.ChunkSize;
        int expected = (int)Math.Min(chunkSize, area.End - area.Start - index * chunkSize);
        byte[] output = _cache.Pool.Rent(expected);
        try
        {
            await DecodeGroupAsync(GroupFor(area, index), expected, 0, area.Start + index * chunkSize, output, null);
            return new Block(output, expected);
        }
        catch
        {
            _cache.Pool.Return(output);
            throw;
        }
    }

    private async ValueTask<Block> DecodeRegionAsync(Area area, long index)
    {
        long firstSector = index * WiiRegion.Sectors;
        int sectors = (int)Math.Min(WiiRegion.Sectors, area.Sectors - firstSector);
        int sectorsPerChunk = Descriptor.ChunkSize / SectorSize;
        long firstChunk = firstSector / sectorsPerChunk, lastChunk = (firstSector + sectors - 1) / sectorsPerChunk;
        byte[] plain = _cache.Pool.Rent(RegionSize);
        byte[] output = _cache.Pool.Rent(sectors * SectorSize);
        var exceptions = new List<HashException>();
        try
        {
            async Task DecodeChunkAsync(long chunk)
            {
                long chunkFirst = chunk * sectorsPerChunk;
                int chunkSectors = (int)Math.Min(sectorsPerChunk, area.Sectors - chunkFirst);
                int payloadBytes = chunkSectors * SectorData;
                Block? cached = null;
                byte[]? payload = null;
                try
                {
                    if (Descriptor.ChunkSize > RegionSize)
                        cached = await GetPartitionGroupAsync(area, chunk);
                    else
                    {
                        payload = _cache.Pool.Rent(payloadBytes);
                        var chunkExceptions = new List<(int List, int Offset, byte[] Hash)>();
                        await DecodeGroupAsync(GroupFor(area, chunk), payloadBytes, ExceptionLists,
                            chunkFirst * SectorData, payload, chunkExceptions);
                        foreach (var (list, offset, hash) in chunkExceptions)
                        {
                            long sector = chunkFirst + ExceptionSector(chunkSectors, list, offset);
                            if (sector >= firstSector && sector < firstSector + sectors)
                                lock (exceptions)
                                    exceptions.Add(new HashException((int)(sector - firstSector), offset & 0x3FF, hash));
                        }
                    }

                    byte[] source = cached?.Buffer ?? payload!;
                    long from = Math.Max(firstSector, chunkFirst), to = Math.Min(firstSector + sectors, chunkFirst + chunkSectors);
                    for (long sector = from; sector < to; sector++)
                        source.AsSpan((int)(sector - chunkFirst) * SectorData, SectorData)
                            .CopyTo(plain.AsSpan((int)(sector - firstSector) * SectorSize + WiiRegion.HashBlock, SectorData));
                    if (cached is not null)
                        for (int at = payloadBytes; at < cached.Length; at += CachedExceptionBytes)
                        {
                            int list = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(at));
                            int offset = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(at + 2));
                            long sector = chunkFirst + ExceptionSector(chunkSectors, list, offset);
                            if (sector >= firstSector && sector < firstSector + sectors)
                                lock (exceptions)
                                    exceptions.Add(new HashException((int)(sector - firstSector), offset & 0x3FF,
                                    source.AsSpan(at + 4, WiiRegion.HashSize).ToArray()));
                        }
                }
                finally
                {
                    if (cached is not null)
                        _cache.Release(cached);
                    if (payload is not null)
                        _cache.Pool.Return(payload);
                }
            }

            if (_options.ParallelRegionDecode && lastChunk > firstChunk)
                await Task.WhenAll(Enumerable.Range(0, (int)(lastChunk - firstChunk + 1)).Select(i => DecodeChunkAsync(firstChunk + i)));
            else
                for (long chunk = firstChunk; chunk <= lastChunk; chunk++)
                    await DecodeChunkAsync(chunk);

            WiiRegion.Build(_keys[area.Partition], plain.AsSpan(0, RegionSize), sectors, exceptions, output.AsSpan(0, sectors * SectorSize));
            return new Block(output, sectors * SectorSize);
        }
        catch
        {
            _cache.Pool.Return(output);
            throw;
        }
        finally { _cache.Pool.Return(plain); }
    }

    // A large Wii source group spans several reconstructed regions. Keep its unpacked payload
    // and exception records together, so reuse and concurrent joins share the cache's byte budget.
    // Negative area IDs cannot collide with the nonnegative reconstructed-region/raw-chunk keys.
    private ValueTask<Block> GetPartitionGroupAsync(Area area, long index) =>
        _cache.GetOrAddAsync(new BlockKey(_owner, ~area.Id, index), (Image: this, Area: area, Index: index),
            static state => state.Image.DecodePartitionGroupOwnedAsync(state.Area, state.Index), CancellationToken.None);

    private async Task<Block> DecodePartitionGroupOwnedAsync(Area area, long index)
    {
        ObjectDisposedException.ThrowIf(!TryBeginDecode(), this);
        byte[]? payload = null;
        try
        {
            int sectorsPerChunk = Descriptor.ChunkSize / SectorSize;
            long firstSector = index * sectorsPerChunk;
            int sectors = (int)Math.Min(sectorsPerChunk, area.Sectors - firstSector);
            int payloadBytes = sectors * SectorData;
            payload = _cache.Pool.Rent(payloadBytes);
            var exceptions = new List<(int List, int Offset, byte[] Hash)>();
            await DecodeGroupAsync(GroupFor(area, index), payloadBytes, ExceptionLists, firstSector * SectorData,
                payload, exceptions);
            int length = checked(payloadBytes + exceptions.Count * CachedExceptionBytes);
            if (length > payload.Length)
            {
                byte[] combined = _cache.Pool.Rent(length);
                payload.AsSpan(0, payloadBytes).CopyTo(combined);
                _cache.Pool.Return(payload);
                payload = combined;
            }
            int at = payloadBytes;
            foreach (var (list, offset, hash) in exceptions)
            {
                _ = ExceptionSector(sectors, list, offset);
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at), (ushort)list);
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at + 2), (ushort)offset);
                hash.CopyTo(payload, at + 4);
                at += CachedExceptionBytes;
            }
            var block = new Block(payload, length);
            payload = null; // The cache owns this buffer from here.
            return block;
        }
        finally
        {
            if (payload is not null)
                _cache.Pool.Return(payload);
            EndDecode();
        }
    }

    private static long ExceptionSector(int sectors, int list, int offset)
    {
        // Each list covers 2 MiB of a large chunk, or the whole of a smaller chunk.
        long sector = (long)list * WiiRegion.Sectors + (offset >> 10);
        if ((offset & 0x3FF) + WiiRegion.HashSize > WiiRegion.HashBlock || sector >= sectors)
            throw new InvalidDataException("An RVZ hash exception is outside its chunk.");
        return sector;
    }

    private int ExceptionLists => Math.Max(1, Descriptor.ChunkSize / WiiGroupSize);

    // Upper bound for a group's stored, packed or decompressed size: payload, exception lists
    // at Dolphin's per-list limit, and generous packing overhead.
    private static int MaxDecodedBytes(int chunkSize) =>
        Math.Max(1, chunkSize / WiiGroupSize) * (2 + MaxExceptions * ExceptionBytes) + 4 + chunkSize + chunkSize / 8 + 4096;

    private Group GroupFor(Area area, long index)
    {
        if (index < 0 || index >= area.GroupCount)
            throw new InvalidDataException("An RVZ chunk index is outside its data area.");
        return _groups[area.FirstGroup + index];
    }

    private async ValueTask DecodeGroupAsync(Group group, int expected, int exceptionLists, long dataOffset,
        byte[] output, List<(int List, int Offset, byte[] Hash)>? exceptions)
    {
        if (group.StoredSize == 0)
        {
            output.AsSpan(0, expected).Clear(); // Every byte is zero and the exception lists are empty.
            return;
        }
        byte[] stored = _cache.Pool.Rent(group.StoredSize);
        try
        {
            await ReadArtifactAsync(_artifact, group.Offset, stored.AsMemory(0, group.StoredSize), CancellationToken.None);
            Decode(group, stored, expected, exceptionLists, dataOffset, output, exceptions);
        }
        finally { _cache.Pool.Return(stored); }
    }

    private void Decode(Group group, byte[] stored, int expected, int exceptionLists, long dataOffset,
        byte[] output, List<(int List, int Offset, byte[] Hash)>? exceptions)
    {
        bool compressed = group.Compressed && _codec is not null;
        byte[]? decompressed = null;
        try
        {
            ReadOnlySpan<byte> data = stored.AsSpan(0, group.StoredSize);
            if (compressed)
            {
                int capacity = MaxDecodedBytes(Descriptor.ChunkSize);
                decompressed = _cache.Pool.Rent(capacity);
                data = decompressed.AsSpan(0, _codec!.Decompress(stored, group.StoredSize, decompressed.AsSpan(0, capacity)));
            }

            int position = 0;
            for (int list = 0; list < exceptionLists; list++)
            {
                if (data.Length - position < 2)
                    throw new InvalidDataException("Truncated RVZ hash exception list.");
                int count = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                position += 2;
                if (count > MaxExceptions || data.Length - position < count * ExceptionBytes)
                    throw new InvalidDataException("An RVZ hash exception list is outside limits.");
                for (int i = 0; i < count; i++, position += ExceptionBytes)
                    exceptions?.Add((list, BinaryPrimitives.ReadUInt16BigEndian(data[position..]),
                        data.Slice(position + 2, WiiRegion.HashSize).ToArray()));
            }
            // Uncompressed groups align the data after their exception lists to 4 bytes.
            if (!compressed && exceptionLists > 0)
                position = (position + 3) & ~3;
            var payload = data[Math.Min(position, data.Length)..];
            if (group.PackedSize != 0)
            {
                if (compressed ? payload.Length != group.PackedSize : payload.Length < group.PackedSize)
                    throw new InvalidDataException("RVZ packed size mismatch.");
                RvzPacking.Unpack(payload[..group.PackedSize], output.AsSpan(0, expected), dataOffset);
            }
            else
            {
                if (compressed ? payload.Length != expected : payload.Length < expected)
                    throw new InvalidDataException("RVZ group size mismatch.");
                payload[..expected].CopyTo(output);
            }
        }
        finally
        {
            if (decompressed is not null)
                _cache.Pool.Return(decompressed);
        }
    }

    private static async Task<byte[]> ReadTableAsync(IRandomAccessContent artifact, RvzCodec? codec, long offset,
        int storedSize, int expectedSize, CancellationToken ct)
    {
        byte[] stored = new byte[storedSize];
        await ReadArtifactAsync(artifact, offset, stored, ct);
        if (codec is null)
        {
            if (storedSize < expectedSize)
                throw new InvalidDataException("Truncated RVZ table.");
            return stored.AsSpan(0, expectedSize).ToArray();
        }
        byte[] table = new byte[expectedSize];
        if (codec.Decompress(stored, storedSize, table) != expectedSize)
            throw new InvalidDataException("RVZ table size mismatch.");
        return table;
    }

    // Truncation is malformed media, not transient I/O pressure.
    private static async ValueTask ReadArtifactAsync(IRandomAccessContent artifact, long offset, Memory<byte> destination,
        CancellationToken ct)
    {
        try
        { await artifact.ReadExactlyAtAsync(offset, destination, ct); }
        catch (EndOfStreamException error) { throw new InvalidDataException("The RVZ artifact is truncated.", error); }
        ct.ThrowIfCancellationRequested(); // A source may complete a read after cancellation; do not decode it.
    }

    private static void Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected, string what)
    {
        Span<byte> actual = stackalloc byte[20];
        SHA1.HashData(data, actual);
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException($"RVZ {what} hash mismatch.");
    }

    private static uint Be32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    private static ulong Be64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        EndDecode(); // Drop the open reference, then join decodes that may still read the artifact.
        await _decodesDrained.Task;
        if (_privateOwner)
            _cache.Evict(_owner);
        _codec?.Dispose();
        await _artifact.DisposeAsync();
    }
}
