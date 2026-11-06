using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Romd.Domain.Hashing;

[InlineArray(32)]
internal struct Sha256Buffer
{
    private byte _0;
}

/// <summary>
///     Represents a 256-bit (32-byte) SHA-256 cryptographic hash value.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct Sha256 : IHashValue<Sha256>
{
    public static int ByteLength => 32;

    private readonly Sha256Buffer _data;

    private Sha256(ReadOnlySpan<byte> source)
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
                   && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref data, 8)) == 0
                   && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref data, 16)) == 0
                   && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref data, 24)) == 0;
        }
    }

    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => IsEmpty ? ReadOnlySpan<byte>.Empty : _data;

    public static Sha256 FromSpan(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength)
        {
            throw new ArgumentException($"SHA-256 must be exactly {ByteLength} bytes, got {data.Length}.",
                nameof(data));
        }

        return new Sha256(data);
    }

    public static bool TryFromSpan(ReadOnlySpan<byte> data, out Sha256 result)
    {
        if (data.Length != ByteLength)
        {
            result = default;
            return false;
        }

        result = new Sha256(data);
        return true;
    }

    /// <summary>
    ///     Parses a hexadecimal string to a Sha256 value.
    /// </summary>
    /// <exception cref="FormatException">Invalid hex string or incorrect length.</exception>
    public static Sha256 Parse(string hex) => HexConverter.Parse<Sha256>(hex);

    /// <summary>
    ///     Attempts to parse a hexadecimal string to a Sha256 value.
    /// </summary>
    public static bool TryParse(string? hex, out Sha256 result) => HexConverter.TryParse(hex, out result);

    /// <summary>
    ///     Converts hash to a new byte array. For EF Core compatibility.
    /// </summary>
    public byte[] ToArray() => IsEmpty ? [] : AsSpan().ToArray();

    /// <summary>
    ///     Creates a Sha256 from a byte array. For EF Core compatibility.
    /// </summary>
    public static Sha256 FromBytes(byte[] bytes) => FromSpan(bytes);

    /// <summary>
    ///     Creates a Sha256 from a byte span. For EF Core compatibility.
    /// </summary>
    public static Sha256 FromBytes(ReadOnlySpan<byte> bytes) => FromSpan(bytes);

    public bool Equals(Sha256 other)
    {
        ref byte left = ref Unsafe.AsRef(in _data[0]);
        ref byte right = ref Unsafe.AsRef(in other._data[0]);

        return Unsafe.ReadUnaligned<long>(ref left) == Unsafe.ReadUnaligned<long>(ref right)
               && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref left, 8)) ==
               Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref right, 8))
               && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref left, 16)) ==
               Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref right, 16))
               && Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref left, 24)) ==
               Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref right, 24));
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Sha256 other && Equals(other);
    public override int GetHashCode() => IsEmpty ? 0 : Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _data[0]));

    public static bool operator ==(Sha256 left, Sha256 right) => left.Equals(right);
    public static bool operator !=(Sha256 left, Sha256 right) => !left.Equals(right);

    public int CompareTo(Sha256 other)
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

    public static bool operator <(Sha256 left, Sha256 right) => left.CompareTo(right) < 0;
    public static bool operator >(Sha256 left, Sha256 right) => left.CompareTo(right) > 0;
    public static bool operator <=(Sha256 left, Sha256 right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Sha256 left, Sha256 right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsEmpty ? string.Empty : Convert.ToHexStringLower(_data);
    public string ToUpperHex() => IsEmpty ? string.Empty : Convert.ToHexString(_data);
}
