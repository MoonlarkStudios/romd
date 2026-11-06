using System.Buffers;

namespace Romd.Domain.Hashing;

/// <summary>
///     Converts between hexadecimal strings and hash values.
/// </summary>
public static class HexConverter
{
    /// <summary>
    ///     Parses a hexadecimal string to a hash value.
    /// </summary>
    /// <exception cref="FormatException">Invalid hex string or incorrect length.</exception>
    public static T Parse<T>(ReadOnlySpan<char> hex)
        where T : struct, IHashValue<T>
    {
        if (hex.Length != T.ByteLength * 2)
        {
            throw new FormatException(
                $"Invalid hex string for {typeof(T).Name}. Expected {T.ByteLength * 2} characters, got {hex.Length}.");
        }

        Span<byte> bytes = stackalloc byte[T.ByteLength];

        if (Convert.FromHexString(hex, bytes, out _, out _) != OperationStatus.Done)
        {
            throw new FormatException($"Invalid hex characters in {typeof(T).Name} string.");
        }

        return T.FromSpan(bytes);
    }

    /// <summary>
    ///     Attempts to parse a hexadecimal string to a hash value.
    /// </summary>
    public static bool TryParse<T>(ReadOnlySpan<char> hex, out T result)
        where T : struct, IHashValue<T>
    {
        result = default;

        if (hex.Length != T.ByteLength * 2)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[T.ByteLength];

        if (Convert.FromHexString(hex, bytes, out _, out _) != OperationStatus.Done)
        {
            return false;
        }

        result = T.FromSpan(bytes);
        return true;
    }

    /// <summary>
    ///     Parses a hexadecimal string to a hash value.
    /// </summary>
    public static T Parse<T>(string hex) where T : struct, IHashValue<T>
        => Parse<T>(hex.AsSpan());

    /// <summary>
    ///     Attempts to parse a hexadecimal string to a hash value.
    /// </summary>
    public static bool TryParse<T>(string? hex, out T result) where T : struct, IHashValue<T>
    {
        if (string.IsNullOrEmpty(hex))
        {
            result = default;
            return false;
        }

        return TryParse(hex.AsSpan(), out result);
    }
}
