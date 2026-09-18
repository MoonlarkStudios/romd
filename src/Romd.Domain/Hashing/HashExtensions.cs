namespace Romd.Domain.Hashing;

/// <summary>
///     Extension methods for hash values.
/// </summary>
public static class HashExtensions
{
    /// <summary>
    ///     Converts hash to a new byte array.
    /// </summary>
    public static byte[] ToArray<T>(this T hash) where T : struct, IHashValue<T>
        => hash.IsEmpty ? [] : hash.AsSpan().ToArray();

    /// <summary>
    ///     Returns the first N characters of the hex representation.
    /// </summary>
    public static string ToShortHex<T>(this T hash, int chars = 8) where T : struct, IHashValue<T>
    {
        if (hash.IsEmpty)
        {
            return string.Empty;
        }

        var span = hash.AsSpan();
        int bytesNeeded = Math.Min((chars + 1) / 2, span.Length);
        return Convert.ToHexStringLower(span[..bytesNeeded])[..Math.Min(chars, bytesNeeded * 2)];
    }
}
