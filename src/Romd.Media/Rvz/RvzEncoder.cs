using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Storage;
using ZstdSharp;

namespace Romd.Media.Rvz;

/// <summary>
/// Creates deterministic GameCube RVZ artifacts with bounded chunk/table buffers. The caller
/// owns both streams. Verification of the result belongs to a separate inspection operation.
/// </summary>
public sealed class RvzEncoder
{
    private const int HeadBytes = 0x48, DiscBytes = 0xDC, HeaderBytes = HeadBytes + DiscBytes;
    private const uint Version = 0x01000000, GameCubeMagic = 0xC2339F3D;
    public static string EncoderBuild { get; } = "romd-media-encoder/mvid:" + typeof(RvzEncoder).Module.ModuleVersionId +
        ";zstdsharp/mvid:" + typeof(Compressor).Module.ModuleVersionId;

    /// <summary>Requires an empty, seekable output; clears it on failure or cancellation.</summary>
    public async Task<RvzEncodeResult> EncodeGameCubeAsync(IRandomAccessContent source, Stream destination,
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
        byte[]? input = null, packed = null, compressed = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (options.InvalidReason() is { } invalid)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidOptions, invalid);
            long length = source.Length;
            if (length <= 0x80)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Truncated GameCube disc header.");
            if (length > options.MaxLogicalBytes)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.LogicalLimitExceeded, "Disc exceeds the logical encoding limit.");
            byte[] discHeader = new byte[0x80];
            if (!await ReadAsync(source, 0, discHeader, ct))
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Invalid or truncated GameCube disc header.");
            if (BinaryPrimitives.ReadUInt32BigEndian(discHeader.AsSpan(0x1C)) != GameCubeMagic ||
                BinaryPrimitives.ReadUInt32BigEndian(discHeader.AsSpan(0x18)) == 0x5D1C9EA3)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.UnsupportedMedia, "Input is not a GameCube disc.");

            int groups = checked((int)((length + options.ChunkBytes - 1) / options.ChunkBytes));
            int tableBytes = checked(groups * 12);
            byte[] table = new byte[tableBytes];
            input = ArrayPool<byte>.Shared.Rent(options.ChunkBytes);
            if (options.PackJunk)
                packed = ArrayPool<byte>.Shared.Rent(options.ChunkBytes);
            // Table compression is independent too. Include its input/output and codec workspace
            // in the bound: inputs never exceed max(chunk size, 12*ceil(logical limit/chunk size)).
            using var compressor = options.Compression == RvzEncodeCompression.Zstandard ? new Compressor(options.CompressionLevel) : null;
            if (compressor is not null)
                compressed = ArrayPool<byte>.Shared.Rent(Compressor.GetCompressBound(Math.Max(options.ChunkBytes, tableBytes)));
            var packing = options.PackJunk ? new RvzPackingEncoder() : null;
            byte[] header = new byte[HeaderBytes], alignment = new byte[3];
            if (!await WriteAsync(destination, header, options.MaxOutputBytes, ct))
                return OutputLimit();
            int packedGroups = 0, compressedGroups = 0, zeroGroups = 0;
            for (int group = 0; group < groups; group++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = (long)group * options.ChunkBytes;
                int take = (int)Math.Min(options.ChunkBytes, length - offset);
                if (!await ReadAsync(source, offset, input.AsMemory(0, take), ct))
                    return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Invalid disc bytes or premature end of the declared length.");
                if (group == 0 && !input.AsSpan(0, discHeader.Length).SequenceEqual(discHeader))
                    return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Disc header changed while encoding.");
                if (input.AsSpan(0, take).IndexOfAnyExcept((byte)0) < 0)
                {
                    zeroGroups++;
                    continue;
                }
                int packedBytes = packing?.Pack(input.AsSpan(0, take), packed!.AsSpan(0, take), offset, ct) ?? 0;
                if (packedBytes > 0)
                    packedGroups++;
                var payload = packedBytes == 0 ? input.AsMemory(0, take) : packed!.AsMemory(0, packedBytes);
                int storedBytes = payload.Length;
                bool useCompression = false;
                if (compressor is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    int encoded = compressor.Wrap(payload.Span, compressed!);
                    ct.ThrowIfCancellationRequested();
                    if (encoded < storedBytes)
                    {
                        storedBytes = encoded;
                        useCompression = true;
                        compressedGroups++;
                    }
                }
                int padding = (int)((4 - destination.Position % 4) % 4);
                if (!await WriteAsync(destination, alignment.AsMemory(0, padding), options.MaxOutputBytes, ct))
                    return OutputLimit();
                long dataOffset = destination.Position;
                if (dataOffset / 4 > uint.MaxValue)
                    return OutputLimit();
                if (!await WriteAsync(destination, useCompression ? compressed!.AsMemory(0, storedBytes) : payload,
                    options.MaxOutputBytes, ct))
                    return OutputLimit();
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(group * 12), (uint)(dataOffset / 4));
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(group * 12 + 4),
                    (uint)storedBytes | (useCompression ? 0x80000000u : 0u));
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(group * 12 + 8), (uint)packedBytes);
            }
            if (source.Length != length)
                return new RvzEncodeResult.Failure(RvzEncodeFailureKind.InvalidSource, "Disc length changed while encoding.");
            byte[] rawTable = new byte[24];
            BinaryPrimitives.WriteUInt64BigEndian(rawTable, 0x80);
            BinaryPrimitives.WriteUInt64BigEndian(rawTable.AsSpan(8), (ulong)(length - 0x80));
            BinaryPrimitives.WriteUInt32BigEndian(rawTable.AsSpan(20), (uint)groups);
            long rawOffset = destination.Position;
            ct.ThrowIfCancellationRequested();
            int rawStored = compressor?.Wrap(rawTable, compressed!) ?? rawTable.Length;
            ct.ThrowIfCancellationRequested();
            if (!await WriteAsync(destination, compressor is null ? rawTable : compressed!.AsMemory(0, rawStored),
                options.MaxOutputBytes, ct))
                return OutputLimit();
            long tableOffset = destination.Position;
            ct.ThrowIfCancellationRequested();
            int tableStored = compressor?.Wrap(table, compressed!) ?? table.Length;
            ct.ThrowIfCancellationRequested();
            if (!await WriteAsync(destination, compressor is null ? table : compressed!.AsMemory(0, tableStored),
                options.MaxOutputBytes, ct))
                return OutputLimit();
            long artifactBytes = destination.Length;
            FillHeader(header, discHeader, options, length, artifactBytes, groups, rawOffset, rawStored, tableOffset, tableStored);
            destination.Position = 0;
            if (!await WriteAsync(destination, header, options.MaxOutputBytes, ct))
                return OutputLimit();
            destination.Position = artifactBytes;
            await destination.FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            complete = true;
            return new RvzEncodeResult.Success(artifactBytes, length, groups, packedGroups, compressedGroups, zeroGroups, EncoderBuild);
        }
        finally
        {
            if (input is not null)
                ArrayPool<byte>.Shared.Return(input);
            if (packed is not null)
                ArrayPool<byte>.Shared.Return(packed);
            if (compressed is not null)
                ArrayPool<byte>.Shared.Return(compressed);
            if (!complete)
            {
                destination.SetLength(0);
                destination.Position = 0;
            }
        }
    }

    private static RvzEncodeResult.Failure OutputLimit() =>
        new(RvzEncodeFailureKind.OutputLimitExceeded, "RVZ output exceeds its byte or format-offset limit.");

    private static async Task<bool> ReadAsync(IRandomAccessContent source, long offset, Memory<byte> target, CancellationToken ct)
    {
        int read = 0;
        while (read < target.Length)
        {
            int got;
            try
            { got = await source.ReadAtAsync(offset + read, target[read..], ct); }
            catch (InvalidDataException) { return false; }
            if (got <= 0 || got > target.Length - read)
                return false;
            read += got;
        }
        return true;
    }

    private static async Task<bool> WriteAsync(Stream destination, ReadOnlyMemory<byte> bytes, long limit, CancellationToken ct)
    {
        if (bytes.Length > limit - destination.Position)
            return false;
        await destination.WriteAsync(bytes, ct);
        return true;
    }

    private static void FillHeader(byte[] header, byte[] discHeader, RvzEncodeOptions options, long logicalBytes,
        long artifactBytes, int groups, long rawOffset, int rawBytes, long tableOffset, int tableBytes)
    {
        var disc = header.AsSpan(HeadBytes, DiscBytes);
        BinaryPrimitives.WriteUInt32BigEndian(disc, 1);
        BinaryPrimitives.WriteUInt32BigEndian(disc[4..], (uint)options.Compression);
        BinaryPrimitives.WriteInt32BigEndian(disc[8..], options.CompressionLevel);
        BinaryPrimitives.WriteUInt32BigEndian(disc[12..], (uint)options.ChunkBytes);
        discHeader.CopyTo(disc[16..]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[148..], 48);
        BinaryPrimitives.WriteUInt64BigEndian(disc[152..], HeaderBytes);
        SHA1.HashData(ReadOnlySpan<byte>.Empty, disc[160..180]);
        BinaryPrimitives.WriteUInt32BigEndian(disc[180..], 1);
        BinaryPrimitives.WriteUInt64BigEndian(disc[184..], (ulong)rawOffset);
        BinaryPrimitives.WriteUInt32BigEndian(disc[192..], (uint)rawBytes);
        BinaryPrimitives.WriteUInt32BigEndian(disc[196..], (uint)groups);
        BinaryPrimitives.WriteUInt64BigEndian(disc[200..], (ulong)tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(disc[208..], (uint)tableBytes);
        "RVZ\u0001"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), DiscBytes);
        SHA1.HashData(disc, header.AsSpan(0x10, 20));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x24), (ulong)logicalBytes);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x2C), (ulong)artifactBytes);
        SHA1.HashData(header.AsSpan(0, 0x34), header.AsSpan(0x34, 20));
    }
}
