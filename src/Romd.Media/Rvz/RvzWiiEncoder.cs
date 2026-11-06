using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Media.Nintendo;
using Romd.Storage;
using ZstdSharp;

namespace Romd.Media.Rvz;

/// <summary>Deterministic retail Wii profile: hashed encrypted sectors, RSA2048 standard/Korean tickets.</summary>
public sealed class RvzWiiEncoder
{
    private readonly WiiTicket _ticket;

    public RvzWiiEncoder() : this(new WiiTicket()) { }

    internal RvzWiiEncoder(WiiTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        _ticket = ticket;
    }

    private const int HeadBytes = 0x48, DiscBytes = 0xDC, HeaderBytes = HeadBytes + DiscBytes;
    public static string EncoderBuild { get; } = "romd-media-encoder/wii;mvid:" + typeof(RvzWiiEncoder).Module.ModuleVersionId +
        ";zstdsharp/mvid:" + typeof(Compressor).Module.ModuleVersionId;

    public async Task<RvzEncodeResult> EncodeAsync(IRandomAccessContent source, Stream destination,
        RvzEncodeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!destination.CanWrite || !destination.CanSeek || destination.Length != 0 || destination.Position != 0)
            throw new ArgumentException("RVZ output must be an empty, writable, seekable stream.", nameof(destination));
        // Establish cleanup support while empty, before taking ownership or reading the source.
        destination.SetLength(0);
        bool complete = false;
        byte[]? rawBuffer = null, payload = null, prefix = null, combined = null, packed = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (options.InvalidReason() is { } invalid)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidOptions, invalid);
            var inspected = await WiiIsoLayout.ReadAsync(source, options.MaxLogicalBytes, ct);
            if (inspected is WiiLayoutResult.Failure failed)
                return failed.Error;
            var layout = ((WiiLayoutResult.Success)inspected).Layout;
            int sectorsPerChunk = options.ChunkBytes / WiiRegion.SectorSize;
            long rawGroups = layout.Raw.Sum(area => (area.End - area.AlignedOffset + options.ChunkBytes - 1) / options.ChunkBytes);
            long partitionGroups = layout.Partitions.Sum(part => (part.Sectors + sectorsPerChunk - 1) / sectorsPerChunk);
            long allGroups = rawGroups + partitionGroups;
            if (allGroups * 12 > 64L << 20)
                return Invalid("Wii group table exceeds reader bounds.");
            int groupCount = checked((int)allGroups), listsPerGroup = Math.Max(1, options.ChunkBytes / WiiPartitionExtractor.RegionBytes);
            int payloadBound = checked(sectorsPerChunk * WiiRegion.SectorData);
            int prefixBound = checked(listsPerGroup * WiiPartitionExtractor.MaximumListBytes);
            // Include every exception list, payload and fallback alignment before taking the
            // compressor bound. At 2 MiB the worst-case input is larger than the declared chunk.
            int inputBound = checked(Math.Max(options.ChunkBytes, payloadBound + prefixBound + 3));
            byte[] groupTable = new byte[checked(groupCount * 12)], rawTable = new byte[layout.Raw.Length * 24];
            byte[] partitionTable = new byte[layout.Partitions.Length * 48], header = new byte[HeaderBytes];
            rawBuffer = ArrayPool<byte>.Shared.Rent(options.ChunkBytes);
            payload = ArrayPool<byte>.Shared.Rent(payloadBound);
            prefix = ArrayPool<byte>.Shared.Rent(prefixBound);
            combined = ArrayPool<byte>.Shared.Rent(inputBound);
            if (options.PackJunk)
                packed = ArrayPool<byte>.Shared.Rent(options.ChunkBytes);
            using var writer = new GroupWriter(destination, options, groupTable,
                Math.Max(inputBound, Math.Max(groupTable.Length, Math.Max(rawTable.Length, partitionTable.Length))));
            var packing = options.PackJunk ? new RvzPackingEncoder() : null;
            if (!await writer.WriteBytesAsync(header, ct))
                return OutputLimit();
            for (int area = 0; area < layout.Raw.Length; area++)
            {
                var extent = layout.Raw[area];
                int firstGroup = writer.Groups;
                for (long at = extent.AlignedOffset; at < extent.End; at += options.ChunkBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int bytes = (int)Math.Min(options.ChunkBytes, extent.End - at);
                    if (!await WiiSourceRead.FillAsync(source, at, rawBuffer.AsMemory(0, bytes), ct))
                        return Invalid("Invalid or truncated Wii raw data.");
                    if (at == 0 && !rawBuffer.AsSpan(0, layout.DiscHeader.Length).SequenceEqual(layout.DiscHeader))
                        return Invalid("Wii disc header changed during encoding.");
                    bool zero = rawBuffer.AsSpan(0, bytes).IndexOfAnyExcept((byte)0) < 0;
                    int packedBytes = zero ? 0 : packing?.Pack(rawBuffer.AsSpan(0, bytes), packed!.AsSpan(), at, ct) ?? 0;
                    ReadOnlyMemory<byte> body = packedBytes == 0 ? rawBuffer.AsMemory(0, bytes) : packed!.AsMemory(0, packedBytes);
                    if (!await writer.WriteGroupAsync(body, 0, packedBytes, zero, ct))
                        return OutputLimit();
                }
                int entry = area * 24;
                BinaryPrimitives.WriteUInt64BigEndian(rawTable.AsSpan(entry), (ulong)extent.Offset);
                BinaryPrimitives.WriteUInt64BigEndian(rawTable.AsSpan(entry + 8), (ulong)extent.Bytes);
                BinaryPrimitives.WriteUInt32BigEndian(rawTable.AsSpan(entry + 16), (uint)firstGroup);
                BinaryPrimitives.WriteUInt32BigEndian(rawTable.AsSpan(entry + 20), (uint)(writer.Groups - firstGroup));
            }
            for (int part = 0; part < layout.Partitions.Length; part++)
            {
                var partition = layout.Partitions[part];
                byte[] titleKey = new byte[16];
                try
                {
                    if (_ticket.TryDecrypt(partition.Ticket, titleKey) is { } keyError)
                        return keyError;
                    titleKey.CopyTo(partitionTable, part * 48);
                    int firstGroup = writer.Groups;
                    using var extractor = new WiiPartitionExtractor(titleKey);
                    if (options.ChunkBytes < WiiPartitionExtractor.RegionBytes)
                    {
                        for (long firstSector = 0; firstSector < partition.Sectors; firstSector += 64)
                        {
                            int regionSectors = (int)Math.Min(64, partition.Sectors - firstSector);
                            if (!await extractor.ReadRegionAsync(source, partition.DataOffset + firstSector * WiiRegion.SectorSize, regionSectors, ct))
                                return Invalid("Invalid or truncated Wii encrypted data.");
                            for (int inRegion = 0; inRegion < regionSectors; inRegion += sectorsPerChunk)
                            {
                                ct.ThrowIfCancellationRequested();
                                int sectors = Math.Min(sectorsPerChunk, regionSectors - inRegion), bytes = sectors * WiiRegion.SectorData;
                                extractor.CopyPayload(inRegion, sectors, payload.AsSpan(0, bytes));
                                int exceptionBytes = extractor.WriteExceptions(inRegion, sectors, prefix);
                                bool noExceptions = BinaryPrimitives.ReadUInt16BigEndian(prefix) == 0;
                                int packedBytes = packing?.Pack(payload.AsSpan(0, bytes), packed!.AsSpan(),
                                    (firstSector + inRegion) * WiiRegion.SectorData, ct) ?? 0;
                                int prefixBytes = PrepareGroup(prefix.AsSpan(0, exceptionBytes), payload.AsSpan(0, bytes),
                                    packed, packedBytes, combined);
                                int storedInput = prefixBytes + (packedBytes == 0 ? bytes : packedBytes);
                                bool zero = noExceptions && payload.AsSpan(0, bytes).IndexOfAnyExcept((byte)0) < 0;
                                if (!await writer.WriteGroupAsync(combined.AsMemory(0, storedInput), prefixBytes, packedBytes, zero, ct))
                                    return OutputLimit();
                            }
                        }
                    }
                    else
                    {
                        for (long firstSector = 0; firstSector < partition.Sectors; firstSector += sectorsPerChunk)
                        {
                            ct.ThrowIfCancellationRequested();
                            int sectors = (int)Math.Min(sectorsPerChunk, partition.Sectors - firstSector), prefixBytes = 0;
                            bool noExceptions = true;
                            for (int list = 0; list < listsPerGroup; list++)
                            {
                                int regionSectors = Math.Min(64, Math.Max(0, sectors - list * 64));
                                if (regionSectors == 0)
                                {
                                    prefix.AsSpan(prefixBytes, 2).Clear();
                                    prefixBytes += 2;
                                    continue;
                                }
                                if (!await extractor.ReadRegionAsync(source,
                                    partition.DataOffset + (firstSector + list * 64) * WiiRegion.SectorSize, regionSectors, ct))
                                    return Invalid("Invalid or truncated Wii encrypted data.");
                                extractor.CopyPayload(0, regionSectors, payload.AsSpan(list * 64 * WiiRegion.SectorData, regionSectors * WiiRegion.SectorData));
                                int written = extractor.WriteExceptions(0, regionSectors, prefix.AsSpan(prefixBytes));
                                noExceptions &= BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(prefixBytes)) == 0;
                                prefixBytes += written;
                            }
                            int bytes = sectors * WiiRegion.SectorData;
                            int packedBytes = packing?.Pack(payload.AsSpan(0, bytes), packed!.AsSpan(), firstSector * WiiRegion.SectorData, ct) ?? 0;
                            PrepareGroup(prefix.AsSpan(0, prefixBytes), payload.AsSpan(0, bytes), packed, packedBytes, combined);
                            int storedInput = prefixBytes + (packedBytes == 0 ? bytes : packedBytes);
                            bool zero = noExceptions && payload.AsSpan(0, bytes).IndexOfAnyExcept((byte)0) < 0;
                            if (!await writer.WriteGroupAsync(combined.AsMemory(0, storedInput), prefixBytes, packedBytes, zero, ct))
                                return OutputLimit();
                        }
                    }
                    int entry = part * 48 + 16;
                    BinaryPrimitives.WriteUInt32BigEndian(partitionTable.AsSpan(entry), checked((uint)(partition.DataOffset / WiiRegion.SectorSize)));
                    BinaryPrimitives.WriteUInt32BigEndian(partitionTable.AsSpan(entry + 4), checked((uint)partition.Sectors));
                    BinaryPrimitives.WriteUInt32BigEndian(partitionTable.AsSpan(entry + 8), (uint)firstGroup);
                    BinaryPrimitives.WriteUInt32BigEndian(partitionTable.AsSpan(entry + 12), (uint)(writer.Groups - firstGroup));
                }
                finally { CryptographicOperations.ZeroMemory(titleKey); }
            }
            if (source.Length != layout.Length)
                return Invalid("Wii disc length changed during encoding.");
            long partitionAt = destination.Position;
            if (!await writer.WriteBytesAsync(partitionTable, ct))
                return OutputLimit();
            long rawAt = destination.Position;
            int rawBytes = await writer.WriteTableAsync(rawTable, ct);
            if (rawBytes < 0)
                return OutputLimit();
            long groupsAt = destination.Position;
            int groupBytes = await writer.WriteTableAsync(groupTable, ct);
            if (groupBytes < 0)
                return OutputLimit();
            long artifactBytes = destination.Length;
            FillHeader(header, layout, options, artifactBytes, partitionTable, partitionAt, rawAt, rawBytes, groupsAt, groupBytes, groupCount);
            destination.Position = 0;
            if (!await writer.WriteBytesAsync(header, ct))
                return OutputLimit();
            destination.Position = artifactBytes;
            await destination.FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            complete = true;
            return new RvzEncodeResult.Success(artifactBytes, layout.Length, writer.Groups, writer.PackedGroups,
                writer.CompressedGroups, writer.ZeroGroups, EncoderBuild);
        }
        finally
        {
            if (rawBuffer is not null)
                ArrayPool<byte>.Shared.Return(rawBuffer);
            if (payload is not null)
                ArrayPool<byte>.Shared.Return(payload);
            if (prefix is not null)
                ArrayPool<byte>.Shared.Return(prefix);
            if (combined is not null)
                ArrayPool<byte>.Shared.Return(combined);
            if (packed is not null)
                ArrayPool<byte>.Shared.Return(packed);
            if (!complete)
            { destination.SetLength(0); destination.Position = 0; }
        }
    }

    private static int PrepareGroup(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload, byte[]? packed, int packedBytes, Span<byte> output)
    {
        prefix.CopyTo(output);
        if (packedBytes == 0)
            payload.CopyTo(output[prefix.Length..]);
        else
            packed!.AsSpan(0, packedBytes).CopyTo(output[prefix.Length..]);
        return prefix.Length;
    }

    private static RvzEncodeResult.Failure Invalid(string description) => new(RvzEncodeFailureKind.InvalidSource, description);
    private static RvzEncodeResult.Failure OutputLimit() => new(RvzEncodeFailureKind.OutputLimitExceeded, "RVZ output exceeds its byte or format-offset limit.");

    private static void FillHeader(byte[] header, WiiIsoLayout layout, RvzEncodeOptions options, long artifactBytes,
        byte[] partitions, long partitionAt, long rawAt, int rawBytes, long groupsAt, int groupBytes, int groups)
    {
        var disc = header.AsSpan(HeadBytes, DiscBytes);
        BinaryPrimitives.WriteUInt32BigEndian(disc, 2);
        BinaryPrimitives.WriteUInt32BigEndian(disc[4..], (uint)options.Compression);
        BinaryPrimitives.WriteInt32BigEndian(disc[8..], options.CompressionLevel);
        BinaryPrimitives.WriteUInt32BigEndian(disc[12..], (uint)options.ChunkBytes);
        layout.DiscHeader.CopyTo(disc[16..]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[144..], (uint)layout.Partitions.Length);
        BinaryPrimitives.WriteUInt32BigEndian(disc[148..], 48);
        BinaryPrimitives.WriteUInt64BigEndian(disc[152..], (ulong)partitionAt);
        SHA1.HashData(partitions, disc[160..180]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[180..], (uint)layout.Raw.Length);
        BinaryPrimitives.WriteUInt64BigEndian(disc[184..], (ulong)rawAt);
        BinaryPrimitives.WriteUInt32BigEndian(disc[192..], (uint)rawBytes);
        BinaryPrimitives.WriteUInt32BigEndian(disc[196..], (uint)groups);
        BinaryPrimitives.WriteUInt64BigEndian(disc[200..], (ulong)groupsAt);
        BinaryPrimitives.WriteUInt32BigEndian(disc[208..], (uint)groupBytes);
        "RVZ\u0001"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x01000000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x01000000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), DiscBytes);
        SHA1.HashData(disc, header.AsSpan(0x10, 20));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x24), (ulong)layout.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x2C), (ulong)artifactBytes);
        SHA1.HashData(header.AsSpan(0, 0x34), header.AsSpan(0x34, 20));
    }

    private sealed class GroupWriter : IDisposable
    {
        private readonly Stream _output;
        private readonly RvzEncodeOptions _options;
        private readonly byte[] _table, _alignment = new byte[3];
        private readonly Compressor? _compressor;
        private readonly byte[]? _compressed;
        public int Groups { get; private set; }
        public int PackedGroups { get; private set; }
        public int CompressedGroups { get; private set; }
        public int ZeroGroups { get; private set; }

        public GroupWriter(Stream output, RvzEncodeOptions options, byte[] table, int inputBound)
        {
            _output = output;
            _options = options;
            _table = table;
            if (options.Compression == RvzEncodeCompression.Zstandard)
            {
                _compressor = new Compressor(options.CompressionLevel);
                try
                { _compressed = ArrayPool<byte>.Shared.Rent(Compressor.GetCompressBound(inputBound)); }
                catch { _compressor.Dispose(); throw; }
            }
        }

        public async Task<bool> WriteGroupAsync(ReadOnlyMemory<byte> input, int prefixBytes, int packedBytes, bool zero, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int entry = Groups * 12;
            if (zero)
            { ZeroGroups++; Groups++; return true; }
            bool compressed = false;
            int size = input.Length;
            if (_compressor is not null)
            {
                int encoded = _compressor.Wrap(input.Span, _compressed!);
                ct.ThrowIfCancellationRequested();
                if (encoded < size)
                { compressed = true; size = encoded; }
            }
            int padding = (int)((4 - _output.Position % 4) % 4);
            if (!await WriteBytesAsync(_alignment.AsMemory(0, padding), ct))
                return false;
            long at = _output.Position;
            if (at / 4 > uint.MaxValue)
                return false;
            if (compressed)
            {
                if (!await WriteBytesAsync(_compressed!.AsMemory(0, size), ct))
                    return false;
                CompressedGroups++;
            }
            else
            {
                // The ENTIRE prefix is padded only for uncompressed groups; packing excludes it.
                int alignment = (4 - prefixBytes % 4) % 4;
                if (!await WriteBytesAsync(input[..prefixBytes], ct) ||
                    !await WriteBytesAsync(_alignment.AsMemory(0, alignment), ct) ||
                    !await WriteBytesAsync(input[prefixBytes..], ct))
                    return false;
                size += alignment;
            }
            BinaryPrimitives.WriteUInt32BigEndian(_table.AsSpan(entry), (uint)(at / 4));
            BinaryPrimitives.WriteUInt32BigEndian(_table.AsSpan(entry + 4), (uint)size | (compressed ? 0x80000000u : 0u));
            BinaryPrimitives.WriteUInt32BigEndian(_table.AsSpan(entry + 8), (uint)packedBytes);
            if (packedBytes > 0)
                PackedGroups++;
            Groups++;
            return true;
        }

        public async Task<int> WriteTableAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_compressor is null)
                return await WriteBytesAsync(bytes, ct) ? bytes.Length : -1;
            int encoded = _compressor.Wrap(bytes.Span, _compressed!);
            ct.ThrowIfCancellationRequested();
            return await WriteBytesAsync(_compressed!.AsMemory(0, encoded), ct) ? encoded : -1;
        }
        public async Task<bool> WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (bytes.Length > _options.MaxOutputBytes - _output.Position)
                return false;
            await _output.WriteAsync(bytes, ct);
            return true;
        }
        public void Dispose()
        {
            _compressor?.Dispose();
            if (_compressed is not null)
                ArrayPool<byte>.Shared.Return(_compressed);
        }
    }
}
