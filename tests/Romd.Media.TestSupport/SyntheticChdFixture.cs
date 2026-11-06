using System.Buffers.Binary;
using Romd.Storage;

namespace Romd.Media.TestSupport;

/// <summary>Synthetic v5 compressed maps; stored hunks carry a real CRC16.</summary>
public sealed record SyntheticChdFixture(byte[] Bytes, long LogicalBytes, int HunkBytes,
    int PayloadOffset, byte Seed, bool RepeatFirst)
{
    public static SyntheticChdFixture Create(int hunks = 3, int finalBytes = 997, byte seed = 7,
        int hunkBytes = 4096, bool repeatFirst = false)
    {
        long logical = checked((long)(hunks - 1) * hunkBytes + finalBytes);
        var bits = new BitWriter();
        for (int code = 0; code < 16; code++)
            bits.Write(4, 4);
        for (int index = 0; index < hunks; index++)
            bits.Write(repeatFirst && index != 0 ? 5U : 4U, 4);
        for (int index = 0; index < hunks; index++)
            if (repeatFirst && index != 0)
                bits.Write(0, 24);
            else
                bits.Write(Crc16(Hunk(index, hunkBytes, seed)), 16);
        byte[] packed = bits.Bytes;
        int payloadOffset = 124 + 16 + packed.Length;
        byte[] file = new byte[checked(payloadOffset + (repeatFirst ? 1 : hunks) * hunkBytes)];
        byte[] rawMap = new byte[checked(hunks * 12)];
        long current = payloadOffset;
        for (int index = 0; index < hunks; index++)
        {
            Span<byte> entry = rawMap.AsSpan(index * 12, 12);
            if (repeatFirst && index != 0)
                entry[0] = 5;
            else
            {
                entry[0] = 4;
                Write24(entry[1..], (uint)hunkBytes);
                Write48(entry[4..], (ulong)current);
                byte[] hunk = Hunk(index, hunkBytes, seed);
                BinaryPrimitives.WriteUInt16BigEndian(entry[10..], Crc16(hunk));
                hunk.CopyTo(file, (int)current);
                current += hunkBytes;
            }
        }
        "MComprHD"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 5);
        "zlib"u8.CopyTo(file.AsSpan(16));
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(32), (ulong)logical);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(40), 124);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(60), 2048);
        Span<byte> map = file.AsSpan(124, 16);
        BinaryPrimitives.WriteUInt32BigEndian(map, (uint)packed.Length);
        Write48(map[4..], (ulong)payloadOffset);
        BinaryPrimitives.WriteUInt16BigEndian(map[10..], Crc16(rawMap));
        map[12] = 16;
        map[13] = 24;
        map[14] = 8;
        packed.CopyTo(file, 140);
        return new(file, logical, hunkBytes, payloadOffset, seed, repeatFirst);
    }

    public byte[] Expected(long offset, int count)
    {
        int length = (int)Math.Min(count, Math.Max(0, LogicalBytes - offset));
        byte[] expected = new byte[length];
        for (int index = 0; index < length; index++)
        {
            long position = offset + index;
            long hunk = RepeatFirst ? 0 : position / HunkBytes;
            expected[index] = (byte)((hunk * 73 + position % HunkBytes * 29 + Seed) % 251);
        }
        return expected;
    }

    public CountedRawContent Source() => new(Bytes, PayloadOffset, HunkBytes);

    private static byte[] Hunk(int index, int size, byte seed)
    {
        byte[] bytes = new byte[size];
        for (int at = 0; at < size; at++)
            bytes[at] = (byte)((index * 73L + at * 29L + seed) % 251);
        return bytes;
    }

    private static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021;
            crc &= 0xFFFF;
        }
        return (ushort)crc;
    }

    private static void Write24(Span<byte> target, uint value)
    { target[0] = (byte)(value >> 16); target[1] = (byte)(value >> 8); target[2] = (byte)value; }

    private static void Write48(Span<byte> target, ulong value)
    { BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)(value >> 32)); BinaryPrimitives.WriteUInt32BigEndian(target[2..], (uint)value); }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _used = 8;
        public void Write(uint value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                if (_used == 8)
                { _bytes.Add(0); _used = 0; }
                if (((value >> bit) & 1) != 0)
                    _bytes[^1] |= (byte)(0x80 >> _used);
                _used++;
            }
        }
        public byte[] Bytes => [.. _bytes];
    }
}

public sealed class CountedRawContent(byte[] bytes, int payloadOffset, int hunkBytes) : ISynchronousRawContent
{
    private readonly ManualResetEventSlim _release = new(true);
    private int _disposed, _payloadReads, _active, _peakActive, _failures;
    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DisposeCalls { get; private set; }
    public int PayloadReads => Volatile.Read(ref _payloadReads);
    public int PeakActive => Volatile.Read(ref _peakActive);
    public long Length => bytes.Length;

    private long? _blockedOffset;

    public void Arm(bool block = true, int failures = 0, int? blockAtHunk = null)
    {
        _blockedOffset = blockAtHunk is { } index ? payloadOffset + (long)index * hunkBytes : null;
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _payloadReads, 0);
        Volatile.Write(ref _failures, failures);
        if (block)
            _release.Reset();
        else
            _release.Set();
    }

    public void Release() => _release.Set();

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= bytes.Length || destination.IsEmpty)
            return 0;
        bool payload = offset >= payloadOffset;
        bool gated = payload && (_blockedOffset is null || offset == _blockedOffset);
        int active = 0;
        if (payload)
        {
            Interlocked.Increment(ref _payloadReads);
            active = Interlocked.Increment(ref _active);
            int peak;
            while (active > (peak = Volatile.Read(ref _peakActive)) &&
                   Interlocked.CompareExchange(ref _peakActive, active, peak) != peak)
            { }
            if (gated)
                Entered.TrySetResult();
        }
        try
        {
            if (payload)
            {
                if (gated && !_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Synthetic payload gate timed out.");
                if (Interlocked.Decrement(ref _failures) >= 0)
                    throw new IOException("Synthetic payload read failure.");
            }
            int length = Math.Min(destination.Length, bytes.Length - (int)offset);
            bytes.AsSpan((int)offset, length).CopyTo(destination);
            return length;
        }
        finally { if (payload) Interlocked.Decrement(ref _active); }
    }

    public void Dispose()
    {
        DisposeCalls++;
        Volatile.Write(ref _disposed, 1);
        _release.Set();
    }
}
