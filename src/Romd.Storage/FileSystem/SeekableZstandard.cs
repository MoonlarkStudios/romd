using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace Romd.Storage.FileSystem;

/// <summary>
/// Standard Zstandard seekable format, with mandatory per-frame checksums in ROMD's writer.
/// Reads are self-validating: every frame but the last decodes to one uniform size, and the table
/// must account exactly for the file around it. No configured limit applies to reading.
/// </summary>
internal static class SeekableZstandard
{
    internal const string Extension = ".szst";
    internal const int MinimumFrameSize = 64 * 1024;
    internal const int MaximumFrameSize = 1024 * 1024;
    private const uint TableMagic = 0x184D2A5E;
    private const uint FooterMagic = 0x8F92EAB1;
    private const int EntrySize = 12;
    // Bounds table buffering at 768 KiB while reading typical tables in one or two calls.
    private const int EntriesPerRead = 65_536;

    // The table's 32-bit length field, which covers the entries plus 9 footer bytes, caps the count.
    private const uint MaximumFrameCount = (uint.MaxValue - 9) / EntrySize;

    internal readonly record struct Frame(long Offset, long LogicalOffset, int StoredSize, int Size, uint Checksum);

    /// <summary>Uniform frames make each logical position derivable; Offsets ends with the table offset.</summary>
    internal sealed record Index(int FrameSize, long Length, long[] Offsets, uint[] Checksums)
    {
        internal int FindFrame(long offset) => (int)(offset / FrameSize);

        /// <summary>Bytes the table's arrays retain.</summary>
        internal long RetainedBytes => Offsets.LongLength * sizeof(long) + Checksums.LongLength * sizeof(uint);

        internal Frame GetFrame(int index)
        {
            long logicalOffset = (long)index * FrameSize;
            return new Frame(Offsets[index], logicalOffset, (int)(Offsets[index + 1] - Offsets[index]),
                (int)Math.Min(FrameSize, Length - logicalOffset), Checksums[index]);
        }
    }

    internal static async Task<long> WriteAsync(Stream source, Stream destination, int frameSize,
        int level, long maxSize, StorageKey key, IProgress<StoreProgress>? progress, long? totalSize, CancellationToken ct)
    {
        using var compressor = new Compressor(level);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] input = ArrayPool<byte>.Shared.Rent(frameSize);
        byte[] output = ArrayPool<byte>.Shared.Rent(Compressor.GetCompressBound(frameSize));
        var entries = new List<(int Stored, int Size, uint Checksum)>();
        long total = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int size = await ReadFrameAsync(source, input.AsMemory(0, frameSize), ct).ConfigureAwait(false);
                if (size == 0)
                    break;
                if (size > maxSize - total || entries.Count == MaximumFrameCount)
                    throw new InvalidOperationException("Content exceeds seekable storage limits.");
                hash.AppendData(input.AsSpan(0, size));
                int stored = compressor.Wrap(input.AsSpan(0, size), output);
                await destination.WriteAsync(output.AsMemory(0, stored), ct).ConfigureAwait(false);
                entries.Add((stored, size, (uint)XxHash64.HashToUInt64(input.AsSpan(0, size))));
                total += size;
                progress?.Report(new StoreProgress
                {
                    Phase = StorePhase.Compressing,
                    BytesProcessed = total,
                    TotalBytes = totalSize
                });
                // Only the last frame may be short; the reader rejects anything else.
                if (size < frameSize)
                    break;
            }

            var actual = Sha256.FromSpan(hash.GetHashAndReset());
            if (actual != key.Hash)
                throw new ContentCorruptedException(key, key.Hash, actual);

            // Buffer the small index to avoid one write syscall per entry.
            byte[] table = new byte[TableSize(entries.Count)];
            WriteTable(CollectionsMarshal.AsSpan(entries), table);
            await destination.WriteAsync(table, ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>Bytes <see cref="Encode"/> may write for <paramref name="length"/> bytes of content.</summary>
    internal static int EncodedBound(int length, int frameSize)
    {
        int remainder = length % frameSize;
        return checked(length / frameSize * Compressor.GetCompressBound(frameSize) +
                       (remainder > 0 ? Compressor.GetCompressBound(remainder) : 0) +
                       TableSize(FrameCount(length, frameSize)));
    }

    /// <summary>
    /// Verifies content held in memory against <paramref name="key"/> and encodes it exactly as
    /// <see cref="WriteAsync"/> frames it, returning the encoded length. The destination must hold
    /// <see cref="EncodedBound"/> bytes.
    /// </summary>
    internal static int Encode(ReadOnlySpan<byte> content, Span<byte> destination, int frameSize, int level,
        StorageKey key)
    {
        Span<byte> hash = stackalloc byte[Sha256.ByteLength];
        SHA256.HashData(content, hash);
        var actual = Sha256.FromSpan(hash);
        if (actual != key.Hash)
            throw new ContentCorruptedException(key, key.Hash, actual);

        using var compressor = new Compressor(level);
        var entries = new (int Stored, int Size, uint Checksum)[FrameCount(content.Length, frameSize)];
        int written = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var frame = content.Slice(i * frameSize, Math.Min(frameSize, content.Length - i * frameSize));
            int stored = compressor.Wrap(frame, destination[written..]);
            entries[i] = (stored, frame.Length, (uint)XxHash64.HashToUInt64(frame));
            written += stored;
        }
        int tableSize = TableSize(entries.Length);
        WriteTable(entries, destination.Slice(written, tableSize));
        return written + tableSize;
    }

    private static int FrameCount(int length, int frameSize) => length / frameSize + (length % frameSize > 0 ? 1 : 0);

    private static int TableSize(int frames) => checked(17 + frames * EntrySize);

    /// <summary>Writes the skippable seek table, with per-frame checksums, into a span of exactly its size.</summary>
    private static void WriteTable(ReadOnlySpan<(int Stored, int Size, uint Checksum)> entries, Span<byte> table)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(table, TableMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(table[4..], (uint)(table.Length - 8));
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = table.Slice(8 + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)entries[i].Stored);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)entries[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], entries[i].Checksum);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(table[^9..], (uint)entries.Length);
        table[^5] = 0x80; // Checksum_Flag
        BinaryPrimitives.WriteUInt32LittleEndian(table[^4..], FooterMagic);
    }

    /// <summary>
    /// Compresses up to <paramref name="frames"/> leading frames exactly as <see cref="WriteAsync"/> frames them,
    /// discarding the output, and returns their compressed to uncompressed ratio (1 for empty content).
    /// </summary>
    internal static async Task<double> SampleRatioAsync(Stream source, int frameSize, int level, int frames,
        CancellationToken ct)
    {
        using var compressor = new Compressor(level);
        byte[] input = ArrayPool<byte>.Shared.Rent(frameSize);
        byte[] output = ArrayPool<byte>.Shared.Rent(Compressor.GetCompressBound(frameSize));
        long stored = 0, total = 0;
        try
        {
            for (int frame = 0; frame < frames; frame++)
            {
                ct.ThrowIfCancellationRequested();
                int size = await ReadFrameAsync(source, input.AsMemory(0, frameSize), ct).ConfigureAwait(false);
                if (size == 0)
                    break;
                stored += compressor.Wrap(input.AsSpan(0, size), output);
                total += size;
                if (size < frameSize)
                    break;
            }
            return total > 0 ? stored / (double)total : 1.0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>Fills one frame from the source; only the end of the source leaves it short.</summary>
    private static async ValueTask<int> ReadFrameAsync(Stream source, Memory<byte> frame, CancellationToken ct)
    {
        int size = 0;
        while (size < frame.Length)
        {
            int read = await source.ReadAsync(frame[size..], ct).ConfigureAwait(false);
            if (read == 0)
                break;
            size += read;
        }
        return size;
    }

    internal static async Task<Index> ReadIndexAsync(SafeFileHandle handle, CancellationToken ct)
    {
        long fileLength = RandomAccess.GetLength(handle);
        if (fileLength < 17)
            throw InvalidIndex();
        byte[] footer = new byte[9];
        await ReadExactlyAsync(handle, footer, fileLength - 9, ct).ConfigureAwait(false);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        long tableSize = 17L + (long)count * EntrySize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(5)) != FooterMagic ||
            footer[4] != 0x80 || count > MaximumFrameCount || tableSize > fileLength)
            throw InvalidIndex();
        long tableOffset = fileLength - tableSize;
        // The magic and exact length where the footer places the table reject a corrupted count before
        // anything is allocated in proportion to it. Only ROMD writes the CAS root, so this defends
        // against corruption; allocation then matches the size of the object's own table.
        byte[] header = new byte[8];
        await ReadExactlyAsync(handle, header, tableOffset, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != TableMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != tableSize - 8)
            throw InvalidIndex();

        var offsets = new long[count + 1];
        var checksums = new uint[count];
        int frameSize = MaximumFrameSize; // Replaced by the first frame; an empty object has none.
        long storedOffset = 0, length = 0;
        byte[] entries = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, EntriesPerRead) * EntrySize);
        try
        {
            for (int first = 0; first < count; first += EntriesPerRead)
            {
                int batch = (int)Math.Min(EntriesPerRead, count - first);
                await ReadExactlyAsync(handle, entries.AsMemory(0, batch * EntrySize),
                    tableOffset + 8 + (long)first * EntrySize, ct).ConfigureAwait(false);
                for (int i = 0; i < batch; i++)
                {
                    var entry = entries.AsSpan(i * EntrySize, EntrySize);
                    uint stored = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                    int frame = first + i;
                    bool last = frame == count - 1;
                    if (frame == 0)
                    {
                        if (size is 0 or > MaximumFrameSize || (!last && size < MinimumFrameSize))
                            throw InvalidIndex();
                        frameSize = (int)size;
                    }
                    if (size == 0 || (last ? size > frameSize : size != frameSize) || stored == 0 ||
                        stored > Compressor.GetCompressBound((int)size) || stored > tableOffset - storedOffset)
                        throw InvalidIndex();
                    offsets[frame] = storedOffset;
                    checksums[frame] = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                    storedOffset += stored;
                    length += size;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(entries);
        }
        if (storedOffset != tableOffset)
            throw InvalidIndex();
        offsets[count] = storedOffset;
        return new Index(frameSize, length, offsets, checksums);
    }

    internal static async ValueTask ReadExactlyAsync(SafeFileHandle handle, Memory<byte> bytes, long offset,
        CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        {
            int read = await RandomAccess.ReadAsync(handle, bytes, offset, ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Truncated CAS object.");
            bytes = bytes[read..];
            offset += read;
        }
    }

    private static InvalidDataException InvalidIndex() => new("Invalid or unsupported CAS seek table.");
}
