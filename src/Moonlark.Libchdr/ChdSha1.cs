using System.Buffers.Binary;

namespace Moonlark.Libchdr;

/// <summary>An immutable twenty-byte SHA-1 value in CHD byte order.</summary>
public readonly record struct ChdSha1
{
    private readonly ulong _first;
    private readonly ulong _second;
    private readonly uint _last;

    private ChdSha1(ReadOnlySpan<byte> bytes)
    {
        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _last = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
    }

    /// <summary>The number of bytes in a SHA-1 value.</summary>
    public const int ByteLength = 20;

    /// <summary>Whether every byte is zero, as for an absent parent hash.</summary>
    public bool IsEmpty => (_first | _second | _last) == 0;

    /// <summary>Copies exactly twenty bytes into a value without retaining the source.</summary>
    /// <param name="bytes">The SHA-1 bytes in network order.</param>
    /// <returns>The immutable hash.</returns>
    /// <exception cref="ArgumentException">The input is not twenty bytes long.</exception>
    public static ChdSha1 FromBytes(ReadOnlySpan<byte> bytes) => bytes.Length == ByteLength
        ? new(bytes) : throw new ArgumentException("A CHD SHA-1 requires exactly twenty bytes.", nameof(bytes));

    /// <summary>Parses exactly forty ASCII hexadecimal characters.</summary>
    /// <param name="text">The hash text.</param>
    /// <param name="value">The parsed value, or the zero value on failure.</param>
    /// <returns>Whether all forty characters are valid.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out ChdSha1 value)
    {
        value = default;
        if (text.Length != ByteLength * 2)
            return false;
        Span<byte> bytes = stackalloc byte[ByteLength];
        for (int index = 0; index < bytes.Length; index++)
        {
            int high = HexDigit(text[index * 2]), low = HexDigit(text[index * 2 + 1]);
            if ((high | low) < 0)
                return false;
            bytes[index] = (byte)((high << 4) | low);
        }
        value = new(bytes);
        return true;
    }

    /// <summary>Writes the twenty bytes in network order.</summary>
    /// <param name="destination">A buffer of at least twenty bytes.</param>
    /// <returns>Whether the destination is large enough. A short buffer is unchanged.</returns>
    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
            return false;
        BinaryPrimitives.WriteUInt64BigEndian(destination, _first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _second);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _last);
        return true;
    }

    /// <summary>Writes forty lowercase ASCII hexadecimal characters without allocating.</summary>
    /// <param name="destination">The character buffer.</param>
    /// <param name="charsWritten">Forty on success, zero on failure.</param>
    /// <returns>Whether the destination is large enough. A short buffer is unchanged.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (destination.Length < ByteLength * 2)
            return false;
        Span<byte> bytes = stackalloc byte[ByteLength];
        TryWriteBytes(bytes);
        const string digits = "0123456789abcdef";
        for (int index = 0; index < bytes.Length; index++)
        {
            destination[index * 2] = digits[bytes[index] >> 4];
            destination[index * 2 + 1] = digits[bytes[index] & 15];
        }
        charsWritten = ByteLength * 2;
        return true;
    }

    /// <summary>Returns the lowercase forty-character representation.</summary>
    /// <returns>The hexadecimal hash.</returns>
    public override string ToString() => string.Create(ByteLength * 2, this,
        static (destination, value) => value.TryFormat(destination, out _));

    private static int HexDigit(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };

    internal int CompareBytes(ChdSha1 other)
    {
        int first = _first.CompareTo(other._first);
        if (first != 0)
            return first;
        int second = _second.CompareTo(other._second);
        return second != 0 ? second : _last.CompareTo(other._last);
    }
}
