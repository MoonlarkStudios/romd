using System.Buffers.Text;
using System.Text;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     The opaque path identity of one entry in a reviewed DAT document pair: unpadded base64url of the
///     entry name's UTF-8 bytes. Raw names are unsafe in a path segment, because Kestrel leaves
///     <c>%2F</c> encoded while decoding <c>%25</c>, so <c>a/b</c> and a literal <c>a%2Fb</c> would collide.
/// </summary>
public static class DatEntryId
{
    public const int MaxLength = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(string entryName) => Base64Url.EncodeToString(StrictUtf8.GetBytes(entryName));

    /// <summary>Decodes only the canonical encoding of a non-empty UTF-8 name.</summary>
    public static bool TryDecode(string? entryId, out string entryName)
    {
        entryName = "";
        if (entryId is not { Length: > 0 and <= MaxLength } || !Base64Url.IsValid(entryId))
            return false;

        try
        {
            string decoded = StrictUtf8.GetString(Base64Url.DecodeFromChars(entryId));
            if (decoded.Length == 0 || Encode(decoded) != entryId)
                return false;
            entryName = decoded;
            return true;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or FormatException)
        {
            return false;
        }
    }
}
