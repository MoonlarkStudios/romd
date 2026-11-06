using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Romd.Application.Common.Pagination;

/// <summary>
///     Utility methods for encoding and decoding pagination cursors.
///     Uses Base64Url encoding (RFC 4648) for URL-safe cursor strings.
/// </summary>
public static class CursorUtils
{
    /// <summary>
    ///     Encodes cursor data as a URL-safe Base64Url string.
    /// </summary>
    public static string ToCursor<TCursor>(TCursor cursorData)
    {
        var json = JsonSerializer.Serialize(cursorData);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    ///     Decodes a cursor string back to cursor data.
    ///     Returns default if the cursor is null, empty, or invalid.
    /// </summary>
    public static TCursor? FromCursor<TCursor>(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return default;
        }

        try
        {
            var bytes = Base64UrlDecode(cursor);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<TCursor>(json);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    ///     Reads a cursor only in its one canonical encoding: at most <paramref name="maxLength" /> base64url
    ///     characters of strict UTF-8 JSON that re-encodes to exactly <paramref name="cursor" />
    ///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity").
    /// </summary>
    public static bool TryReadCanonical<TCursor>(string? cursor, int maxLength, [NotNullWhen(true)] out TCursor? value)
        where TCursor : class
    {
        value = null;
        if (cursor is not { Length: > 0 } || cursor.Length > maxLength
            || !cursor.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return false;

        TCursor? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<TCursor>(StrictUtf8.GetString(Base64UrlDecode(cursor)));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or JsonException)
        {
            return false;
        }

        if (decoded is null || ToCursor(decoded) != cursor)
            return false;
        value = decoded;
        return true;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    ///     Encodes bytes to Base64Url (RFC 4648) - URL-safe with no padding.
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);

        // Replace URL-unsafe characters and remove padding
        return base64
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    ///     Decodes Base64Url (RFC 4648) back to bytes.
    /// </summary>
    private static byte[] Base64UrlDecode(string base64Url)
    {
        // Restore URL-unsafe characters
        var base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        // Add padding if needed
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        return Convert.FromBase64String(base64);
    }
}
