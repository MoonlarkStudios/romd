using System.Text;

namespace Romd.Admin.Application.Titles.Commands.UploadTitleMedia;

/// <summary>
///     Structural acceptance of a manual PDF before it is stored. It accepts a complete, unencrypted PDF 1.x or 2.0
///     file of bounded size and rejects files that declare document JavaScript or launch actions in their
///     uncompressed structure. It is not a sanitizer: compressed object streams are not inspected, so delivery
///     never executes document scripts either (see docs/architecture/artwork-pipeline.md, "Manuals").
/// </summary>
public static class ManualDocument
{
    public const string ContentType = "application/pdf";
    public const long MaxBytes = 128L * 1024 * 1024;
    private const int HeaderWindow = 1024;
    private const int TrailerWindow = 4096;

    private static readonly byte[][] Refused =
    [
        "/Encrypt"u8.ToArray(),
        "/JavaScript"u8.ToArray(),
        "/Launch"u8.ToArray()
    ];

    public static async Task<bool> IsAcceptableAsync(Stream content, long length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (length is < 64 or > MaxBytes || !content.CanSeek)
            return false;

        content.Position = 0;
        var head = new byte[Math.Min(HeaderWindow, length)];
        await content.ReadExactlyAsync(head, ct);
        if (!HasHeader(head))
            return false;

        content.Position = length - Math.Min(TrailerWindow, length);
        var tail = new byte[Math.Min(TrailerWindow, length)];
        await content.ReadExactlyAsync(tail, ct);
        if (IndexOf(tail, "startxref"u8) < 0 || LastIndexOf(tail, "%%EOF"u8) < 0)
            return false;

        content.Position = 0;
        return !await ContainsRefusedTokenAsync(content, ct);
    }

    private static bool HasHeader(ReadOnlySpan<byte> head)
    {
        var at = IndexOf(head, "%PDF-"u8);
        if (at < 0 || head.Length < at + 8)
            return false;
        var version = Encoding.ASCII.GetString(head.Slice(at + 5, 3));
        return version is "1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "2.0";
    }

    private static async Task<bool> ContainsRefusedTokenAsync(Stream content, CancellationToken ct)
    {
        var overlap = Refused.Max(token => token.Length) - 1;
        var buffer = new byte[(1 << 20) + overlap];
        var carried = 0;
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried), ct)) > 0)
        {
            var window = buffer.AsSpan(0, carried + read);
            foreach (var token in Refused)
                if (IsNameAt(window, token))
                    return true;
            carried = Math.Min(overlap, window.Length);
            window[^carried..].CopyTo(buffer);
        }

        return false;
    }

    /// <summary>A PDF name matches only when the next byte ends it, so /JavaScriptFoo is not /JavaScript.</summary>
    private static bool IsNameAt(ReadOnlySpan<byte> window, ReadOnlySpan<byte> token)
    {
        var start = 0;
        while (start < window.Length)
        {
            var at = window[start..].IndexOf(token);
            if (at < 0)
                return false;
            var end = start + at + token.Length;
            if (end >= window.Length || IsDelimiter(window[end]))
                return true;
            start = end;
        }

        return false;
    }

    private static bool IsDelimiter(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or
        (byte)'\f' or 0 or (byte)'/' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or
        (byte)'{' or (byte)'}' or (byte)'%';

    private static int IndexOf(ReadOnlySpan<byte> value, ReadOnlySpan<byte> token) => value.IndexOf(token);

    private static int LastIndexOf(ReadOnlySpan<byte> value, ReadOnlySpan<byte> token) => value.LastIndexOf(token);
}
