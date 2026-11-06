using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Romd.Domain.Hashing;

[InlineArray(16)]
internal struct Md5Buffer
{
    private byte _0;
}

/// <summary>
///     Represents a 128-bit (16-byte) MD5 hash value.
/// </summary>
/// <remarks>
///     MD5 is cryptographically broken. Use only for legacy compatibility (e.g., DAT files).
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct Md5 : IHashValue<Md5>
{
    public static int ByteLength => 16;

    private readonly Md5Buffer _data;

    private Md5(ReadOnlySpan<byte> source)
    {
        source.CopyTo(_data);
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref byte data = ref Unsafe.AsRef(in _data[0]);
            return Unsafe.ReadUnaligned<long>(ref data) == 0
                   && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref data, 8)) == 0;
        }
    }

    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => IsEmpty ? ReadOnlySpan<byte>.Empty : _data;

    public static Md5 FromSpan(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength)
        {
            throw new ArgumentException($"MD5 must be exactly {ByteLength} bytes, got {data.Length}.", nameof(data));
        }

        return new Md5(data);
    }

    public static bool TryFromSpan(ReadOnlySpan<byte> data, out Md5 result)
    {
        if (data.Length != ByteLength)
        {
            result = default;
            return false;
        }

        result = new Md5(data);
        return true;
    }

    /// <summary>
    ///     Parses a hexadecimal string to a Md5 value.
    /// </summary>
    /// <exception cref="FormatException">Invalid hex string or incorrect length.</exception>
    public static Md5 Parse(string hex) => HexConverter.Parse<Md5>(hex);

    /// <summary>
    ///     Attempts to parse a hexadecimal string to a Md5 value.
    /// </summary>
    public static bool TryParse(string? hex, out Md5 result) => HexConverter.TryParse(hex, out result);

    /// <summary>
    ///     Converts hash to a new byte array. For EF Core compatibility.
    /// </summary>
    public byte[] ToArray() => IsEmpty ? [] : AsSpan().ToArray();

    /// <summary>
    ///     Creates a Md5 from a byte array. For EF Core compatibility.
    /// </summary>
    public static Md5 FromBytes(byte[] bytes) => FromSpan(bytes);

    /// <summary>
    ///     Creates a Md5 from a byte span. For EF Core compatibility.
    /// </summary>
    public static Md5 FromBytes(ReadOnlySpan<byte> bytes) => FromSpan(bytes);

    public bool Equals(Md5 other)
    {
        ref byte left = ref Unsafe.AsRef(in _data[0]);
        ref byte right = ref Unsafe.AsRef(in other._data[0]);

        return Unsafe.ReadUnaligned<long>(ref left) == Unsafe.ReadUnaligned<long>(ref right)
               && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref left, 8)) ==
               Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref right, 8));
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Md5 other && Equals(other);
    public override int GetHashCode() => IsEmpty ? 0 : Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _data[0]));

    public static bool operator ==(Md5 left, Md5 right) => left.Equals(right);
    public static bool operator !=(Md5 left, Md5 right) => !left.Equals(right);

    public int CompareTo(Md5 other)
    {
        bool leftEmpty = IsEmpty;
        bool rightEmpty = other.IsEmpty;

        if (leftEmpty && rightEmpty)
        {
            return 0;
        }

        if (leftEmpty)
        {
            return -1;
        }

        if (rightEmpty)
        {
            return 1;
        }

        return AsSpan().SequenceCompareTo(other.AsSpan());
    }

    public static bool operator <(Md5 left, Md5 right) => left.CompareTo(right) < 0;
    public static bool operator >(Md5 left, Md5 right) => left.CompareTo(right) > 0;
    public static bool operator <=(Md5 left, Md5 right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Md5 left, Md5 right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsEmpty ? string.Empty : Convert.ToHexStringLower(_data);
    public string ToUpperHex() => IsEmpty ? string.Empty : Convert.ToHexString(_data);
}
