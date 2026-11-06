using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Romd.Domain.Hashing;

[InlineArray(4)]
internal struct Crc32Buffer
{
    private byte _0;
}

/// <summary>
///     Represents a 32-bit CRC32 checksum value stored in big-endian (network) byte order.
/// </summary>
/// <remarks>
///     <para>
///         Unlike cryptographic hashes, CRC32 can legitimately produce all zeros (0x00000000).
///         This type uses an explicit flag to distinguish uninitialized from a zero value.
///     </para>
///     <para>
///         <b>Endianness:</b> This type stores CRC32 in big-endian byte order to match the
///         standard hex representation used in DAT files (No-Intro, Redump, TOSEC).
///         For example, CRC32 value 0xABCD1234 is stored as bytes [AB, CD, 12, 34] and
///         displays as "abcd1234".
///     </para>
///     <para>
///         <b>System.IO.Hashing interop:</b> The .NET System.IO.Hashing.Crc32 class outputs
///         bytes in little-endian order on x86/x64/ARM platforms. Use <see cref="FromLittleEndian" />
///         to convert from System.IO.Hashing output to this type.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct Crc32 : IHashValue<Crc32>
{
    public static int ByteLength => 4;

    private readonly Crc32Buffer _data;
    private readonly bool _hasValue;

    private Crc32(ReadOnlySpan<byte> source)
    {
        source.CopyTo(_data);
        _hasValue = true;
    }

    public bool IsEmpty => !_hasValue;

    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => _hasValue ? _data : ReadOnlySpan<byte>.Empty;

    /// <summary>
    ///     Creates a CRC32 from bytes already in big-endian order.
    /// </summary>
    /// <param name="data">Exactly 4 bytes in big-endian order.</param>
    /// <exception cref="ArgumentException">Data is not exactly 4 bytes.</exception>
    public static Crc32 FromSpan(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength)
        {
            throw new ArgumentException($"CRC32 must be exactly {ByteLength} bytes, got {data.Length}.", nameof(data));
        }

        return new Crc32(data);
    }

    /// <summary>
    ///     Creates a CRC32 from little-endian bytes (as produced by System.IO.Hashing.Crc32).
    ///     Converts to big-endian for storage and display (DAT file convention).
    /// </summary>
    /// <remarks>
    ///     DAT files from No-Intro, Redump, etc. represent CRC32 as big-endian hex strings.
    ///     System.IO.Hashing.Crc32 outputs native little-endian on x86/x64/ARM.
    ///     This method handles the conversion automatically.
    /// </remarks>
    /// <param name="data">Exactly 4 bytes in little-endian order.</param>
    /// <exception cref="ArgumentException">Data is not exactly 4 bytes.</exception>
    public static Crc32 FromLittleEndian(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength)
        {
            throw new ArgumentException($"CRC32 must be exactly {ByteLength} bytes, got {data.Length}.", nameof(data));
        }

        Span<byte> bigEndian = stackalloc byte[ByteLength];
        data.CopyTo(bigEndian);
        bigEndian.Reverse();
        return new Crc32(bigEndian);
    }

    public static bool TryFromSpan(ReadOnlySpan<byte> data, out Crc32 result)
    {
        if (data.Length != ByteLength)
        {
            result = default;
            return false;
        }

        result = new Crc32(data);
        return true;
    }

    /// <summary>
    ///     Parses a hexadecimal string to a Crc32 value.
    /// </summary>
    /// <exception cref="FormatException">Invalid hex string or incorrect length.</exception>
    public static Crc32 Parse(string hex) => HexConverter.Parse<Crc32>(hex);

    /// <summary>
    ///     Attempts to parse a hexadecimal string to a Crc32 value.
    /// </summary>
    public static bool TryParse(string? hex, out Crc32 result) => HexConverter.TryParse(hex, out result);

    /// <summary>
    ///     Converts hash to a new byte array. For EF Core compatibility.
    /// </summary>
    public byte[] ToArray() => _hasValue ? AsSpan().ToArray() : [];

    /// <summary>
    ///     Creates a Crc32 from a byte array. For EF Core compatibility.
    /// </summary>
    public static Crc32 FromBytes(byte[] bytes) => FromSpan(bytes);

    /// <summary>
    ///     Creates a Crc32 from a byte span. For EF Core compatibility.
    /// </summary>
    public static Crc32 FromBytes(ReadOnlySpan<byte> bytes) => FromSpan(bytes);

    /// <summary>
    ///     Returns the CRC32 as an unsigned 32-bit integer.
    /// </summary>
    /// <exception cref="InvalidOperationException">CRC32 is empty.</exception>
    public uint ToUInt32()
    {
        if (!_hasValue)
        {
            throw new InvalidOperationException("Cannot convert empty CRC32 to uint.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(_data);
    }

    /// <summary>
    ///     Creates a CRC32 from an unsigned 32-bit integer.
    ///     The integer is stored in big-endian byte order.
    /// </summary>
    public static Crc32 FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new Crc32(bytes);
    }

    public bool Equals(Crc32 other)
    {
        if (_hasValue != other._hasValue)
        {
            return false;
        }

        if (!_hasValue)
        {
            return true;
        }

        return Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _data[0]))
               == Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in other._data[0]));
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Crc32 other && Equals(other);
    public override int GetHashCode() => _hasValue ? Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _data[0])) : 0;

    public static bool operator ==(Crc32 left, Crc32 right) => left.Equals(right);
    public static bool operator !=(Crc32 left, Crc32 right) => !left.Equals(right);

    public int CompareTo(Crc32 other)
    {
        if (!_hasValue && !other._hasValue)
        {
            return 0;
        }

        if (!_hasValue)
        {
            return -1;
        }

        if (!other._hasValue)
        {
            return 1;
        }

        return AsSpan().SequenceCompareTo(other.AsSpan());
    }

    public static bool operator <(Crc32 left, Crc32 right) => left.CompareTo(right) < 0;
    public static bool operator >(Crc32 left, Crc32 right) => left.CompareTo(right) > 0;
    public static bool operator <=(Crc32 left, Crc32 right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Crc32 left, Crc32 right) => left.CompareTo(right) >= 0;

    public override string ToString() => _hasValue ? Convert.ToHexStringLower(_data) : string.Empty;
    public string ToUpperHex() => _hasValue ? Convert.ToHexString(_data) : string.Empty;
}
