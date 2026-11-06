using System.Buffers.Binary;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Bounds the central directory before ZipArchive materializes its entries. Never allocate from
/// an archive count or length here; inspect fixed records and skip their bounded variable fields.
/// </summary>
internal static class ZipHeaderGuard
{
    internal const int MaxEntries = 50_000;
    internal const int MaxDirectoryBytes = 16 * 1024 * 1024;
    internal const int MaxNameBytes = 4 * JobItem.MaxFileNameLength;
    private const uint EndSignature = 0x06054b50, Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50, EntrySignature = 0x02014b50;

    internal static void Validate(Stream source, CancellationToken ct)
    {
        if (!source.CanSeek)
            throw new ArgumentException("Archive stream must be seekable", nameof(source));
        ct.ThrowIfCancellationRequested();
        long length = source.Length;
        if (length < 22)
            throw Invalid("Truncated ZIP end record.");
        byte[] tail = new byte[(int)Math.Min(length, 22 + ushort.MaxValue)];
        ReadAt(source, length - tail.Length, tail);
        int end = tail.Length - 22;
        // Match the framework's complete-record search window: numeric fields in the last
        // 18 bytes cannot be candidate signatures. Do not skip a later candidate in a comment.
        while (end >= 0 && U32(tail.AsSpan(end)) != EndSignature)
            end--;
        if (end < 0 || tail.Length - end < 22 || U16(tail.AsSpan(end + 20)) != tail.Length - end - 22)
            throw Invalid("Invalid ZIP end record or comment.");
        var footer = tail.AsSpan(end, 22);
        long endOffset = length - tail.Length + end;
        if (U16(footer[4..]) != 0 || U16(footer[6..]) != 0)
            throw Invalid("Multi-volume ZIP archives are unsupported.");

        ulong count = U16(footer[10..]), size = U32(footer[12..]), offset = U32(footer[16..]);
        ulong diskCount = U16(footer[8..]);
        if (count == ushort.MaxValue || diskCount == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            Span<byte> locator = stackalloc byte[20];
            if (endOffset < locator.Length)
                throw Invalid("Missing ZIP64 locator.");
            ReadAt(source, endOffset - locator.Length, locator);
            if (U32(locator) != Zip64LocatorSignature || U32(locator[4..]) != 0 || U32(locator[16..]) != 1)
                throw Invalid("Invalid or multi-volume ZIP64 locator.");
            ulong recordOffset = U64(locator[8..]);
            if (recordOffset > (ulong)(endOffset - locator.Length) ||
                (ulong)(endOffset - locator.Length) - recordOffset < 56)
                throw Invalid("ZIP64 end record is outside the archive.");
            Span<byte> record = stackalloc byte[56];
            ReadAt(source, (long)recordOffset, record);
            if (U32(record) != Zip64EndSignature || U64(record[4..]) < 44 ||
                U64(record[4..]) != (ulong)(endOffset - locator.Length) - recordOffset - 12 ||
                U32(record[16..]) != 0 || U32(record[20..]) != 0)
                throw Invalid("Invalid or multi-volume ZIP64 end record.");
            ulong actualCount = U64(record[32..]), actualSize = U64(record[40..]), actualOffset = U64(record[48..]);
            if (U64(record[24..]) != actualCount ||
                count != ushort.MaxValue && count != actualCount ||
                diskCount != ushort.MaxValue && diskCount != actualCount ||
                size != uint.MaxValue && size != actualSize || offset != uint.MaxValue && offset != actualOffset)
                throw Invalid("Contradictory ZIP64 directory metadata.");
            count = actualCount;
            size = actualSize;
            offset = actualOffset;
            endOffset = (long)recordOffset;
        }
        else if (diskCount != count)
            throw Invalid("Multi-volume ZIP archives are unsupported.");

        if (count > MaxEntries)
            throw Invalid("ZIP metadata entry limit exceeded (50,000).");
        if (size > MaxDirectoryBytes)
            throw Invalid("ZIP central-directory memory limit exceeded (16 MiB).");
        // Require adjacency, not merely containment: ZipArchive reads consecutive directory
        // entries, so an unchecked second directory after the declared size must be impossible.
        if (offset > (ulong)endOffset || size != (ulong)endOffset - offset)
            throw Invalid("ZIP central directory is outside its declared bounds.");
        long position = (long)offset;
        Span<byte> entry = stackalloc byte[46];
        for (ulong index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (endOffset - position < entry.Length)
                throw Invalid("Truncated ZIP central directory.");
            ReadAt(source, position, entry);
            if (U32(entry) != EntrySignature)
                throw Invalid("Invalid ZIP central-directory entry.");
            int nameBytes = U16(entry[28..]);
            if (nameBytes > MaxNameBytes)
                throw Invalid("ZIP entry name exceeds its metadata memory limit.");
            long recordBytes = entry.Length + nameBytes + U16(entry[30..]) + U16(entry[32..]);
            if (recordBytes > endOffset - position)
                throw Invalid("Truncated ZIP central-directory entry.");
            position += recordBytes;
        }
        if (position != endOffset)
        {
            // The standard optional digital-signature record terminates member parsing in
            // ZipArchive. Its opaque payload may occupy only the remaining bounded directory.
            if (endOffset - position < 6)
                throw Invalid("Truncated ZIP directory signature record.");
            Span<byte> signature = stackalloc byte[6];
            ReadAt(source, position, signature);
            if (U32(signature) != 0x05054b50)
                throw Invalid("ZIP directory entry count or size mismatch.");
            if (U16(signature[4..]) != endOffset - position - signature.Length)
                throw Invalid("Invalid ZIP directory signature length.");
        }
        ct.ThrowIfCancellationRequested();
    }

    private static void ReadAt(Stream source, long offset, Span<byte> destination)
    {
        source.Position = offset;
        try
        { source.ReadExactly(destination); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated ZIP metadata.", error); }
    }

    private static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16LittleEndian(value);
    private static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
    private static ulong U64(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64LittleEndian(value);
    private static InvalidDataException Invalid(string message) => new(message);
}
