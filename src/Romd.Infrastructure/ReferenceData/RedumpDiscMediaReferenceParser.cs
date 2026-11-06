using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.ReferenceData;

/// <summary>Bounded extraction of an explicit PS2 singleton DVD assertion from the current Redump HTML layout.</summary>
/// <remarks>This parser authenticates neither retrieval nor assertions. A trusted retrieval boundary must
/// supply these bytes/provenance after an authenticated, allowlisted fetch and retain the same raw body.
/// HTML, links and scripts are never executed or fetched. Names, sizes and DAT labels do not establish media kind.
/// Raw text and template/noscript contexts cannot supply assertions; plaintext and foreign SVG/math layouts are refused.</remarks>
public static class RedumpDiscMediaReferenceParser
{
    public const string Profile = "redump-disc-singleton-dvd-html/v1";
    public const int MaximumBodyBytes = 1 << 20;
    private const int MaximumTableCharacters = 64 << 10, MaximumTableNodes = 4096, MaximumTableDepth = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Headers = ["Filename", "Size", "CRC-32", "MD5", "SHA-1"];
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
    private static readonly HashSet<string> RawTextTags = new(StringComparer.Ordinal)
        { "script", "style", "title", "textarea", "xmp", "iframe", "noembed", "noframes" };

    public static ErrorOr<RomDiscMediaReferenceRecord> Parse(ReadOnlyMemory<byte> rawUtf8, Guid recordId,
        string canonicalSourceUrl, int discId, DateTimeOffset retrievedAtUtc, string acceptedSourceProvenance,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (recordId == Guid.Empty || discId <= 0 || retrievedAtUtc.Offset != TimeSpan.Zero ||
            canonicalSourceUrl != "https://redump.info/disc/" + discId.ToString(CultureInfo.InvariantCulture) ||
            !SafeLabel(acceptedSourceProvenance, 256))
            return RedumpDiscMediaReferenceErrors.InvalidContext();
        if (rawUtf8.Length is <= 0 or > MaximumBodyBytes)
            return RedumpDiscMediaReferenceErrors.InputLimit();

        // A caller's mutable array must not change the bytes between decoding and raw-provenance hashing.
        byte[] body = rawUtf8.ToArray();
        string html;
        try
        { html = StrictUtf8.GetString(body); }
        catch (DecoderFallbackException) { return RedumpDiscMediaReferenceErrors.InvalidUtf8(); }
        if (html.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            html.Contains("<?", StringComparison.Ordinal) || html.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("<![CDATA[", StringComparison.OrdinalIgnoreCase))
            return RedumpDiscMediaReferenceErrors.Malformed();
        int doctype = html.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        const string htmlDoctype = "<!DOCTYPE html>";
        if (doctype >= 0 && (html[..doctype].Trim(' ', '\t', '\r', '\n', '\uFEFF').Length != 0 ||
            !html.AsSpan(doctype).StartsWith(htmlDoctype, StringComparison.OrdinalIgnoreCase) ||
            html.IndexOf("<!DOCTYPE", doctype + htmlDoctype.Length, StringComparison.OrdinalIgnoreCase) >= 0))
            return RedumpDiscMediaReferenceErrors.Malformed();
        if (!ExtractTables(html, ct, out string? infoText, out string? filesText) ||
            !ReadTable(infoText!, out var info) || !ReadTable(filesText!, out var files) ||
            !SafeLinks(info!) || !SafeLinks(files!))
            return RedumpDiscMediaReferenceErrors.Malformed();

        if (!ReadDisc(info!, out string? system, out string? media))
            return RedumpDiscMediaReferenceErrors.Malformed();
        if (system != "Sony PlayStation 2" || media is not ("DVD-5" or "DVD-9"))
            return RedumpDiscMediaReferenceErrors.Unsupported();
        if (!ReadFile(files!, out long bytes, out var crc32, out var md5, out var sha1))
            return RedumpDiscMediaReferenceErrors.Malformed();
        ct.ThrowIfCancellationRequested();
        return new RomDiscMediaReferenceRecord(recordId, RomDiscReferenceSystem.PlayStation2,
            media == "DVD-5" ? RomDiscReferenceMedia.Dvd5 : RomDiscReferenceMedia.Dvd9, 1,
            bytes, sha1, md5, crc32, body.Length, Sha256.FromSpan(SHA256.HashData(body)),
            canonicalSourceUrl, Profile, acceptedSourceProvenance, retrievedAtUtc);
    }

    private static bool ReadDisc(XElement table, out string? system, out string? media)
    {
        system = media = null;
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Elements())
        {
            if (row.Name != "tr")
                return false;
            var cells = row.Elements().ToArray();
            if (cells.Length != 2 || cells.Any(cell => cell.Name != "td" || cell.Descendants().Any(child =>
                    child.Name.LocalName is "tr" or "td" or "th" or "thead" or "tbody")) ||
                !SimpleText(cells[0], 256, out string label) || !labels.Add(label))
                return false;
            if (label.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                if (!SimpleText(cells[1], 128, out string value))
                    return false;
                system = value;
            }
            else if (label.Equals("Media", StringComparison.OrdinalIgnoreCase))
            {
                if (!SimpleText(cells[1], 128, out string value))
                    return false;
                media = value;
            }
        }
        return system is not null && media is not null;
    }

    private static bool ReadFile(XElement table, out long bytes, out Crc32 crc32, out Md5 md5, out Sha1 sha1)
    {
        bytes = 0;
        crc32 = default;
        md5 = default;
        sha1 = default;
        var sections = table.Elements().ToArray();
        if (sections.Length != 2 || sections[0].Name != "thead" || sections[1].Name != "tbody")
            return false;
        var heading = sections[0].Elements().ToArray();
        var rows = sections[1].Elements().ToArray();
        if (heading.Length != 1 || heading[0].Name != "tr" || rows.Length != 1 || rows[0].Name != "tr" ||
            rows[0].HasAttributes)
            return false; // Synthetic/merged rows are never a published singleton.
        var titles = heading[0].Elements().ToArray();
        var cells = rows[0].Elements().ToArray();
        if (titles.Length != Headers.Length || cells.Length != Headers.Length)
            return false;
        var values = new string[Headers.Length];
        for (int index = 0; index < Headers.Length; index++)
            if (titles[index].Name != "th" || !SimpleText(titles[index], 64, out string title) || title != Headers[index] ||
                cells[index].Name != "td" || !SimpleText(cells[index], index == 0 ? 512 : 64, out values[index]))
                return false;
        return values[1].All(char.IsAsciiDigit) &&
            long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out bytes) &&
            bytes is > 0 and <= RomPs2BootLayoutFacts.MaximumLogicalBytes &&
            Crc32.TryParse(values[2], out crc32) && !crc32.IsEmpty &&
            Md5.TryParse(values[3], out md5) && !md5.IsEmpty && Sha1.TryParse(values[4], out sha1) && !sha1.IsEmpty;
    }

    private static bool SimpleText(XElement element, int maximum, out string text)
    {
        string value = element.Value;
        text = value.Trim();
        if (value.Any(char.IsControl))
            return false;
        return SafeLabel(text, maximum) && element.Descendants().All(child =>
            child.Name == "code" || child.Name == "strong" || child.Name == "a");
    }

    private static bool SafeLabel(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static bool SafeLinks(XElement table)
    {
        if (table.Descendants().Any(element => element.Name.LocalName is "table" or "script" or "style" || element.Name.Namespace != XNamespace.None))
            return false;
        foreach (var element in table.DescendantsAndSelf())
        {
            var links = element.Attributes().Where(attribute => attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (links.Length > 1)
                return false;
            foreach (var link in links)
            {
                string value = link.Value;
                if (!SafeLabel(value, 1024) || value.Contains('\\') || value.StartsWith("//", StringComparison.Ordinal) ||
                    !(value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("https://redump.info/", StringComparison.Ordinal)) ||
                    !Uri.TryCreate(new Uri("https://redump.info/"), value, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "redump.info" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                    return false;
            }
        }
        return table.Name == "table";
    }

    private static bool ReadTable(string fragment, out XElement? table)
    {
        table = null;
        if (fragment.Length > MaximumTableCharacters || !NormalizeTags(fragment, out string normalized))
            return false;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumTableCharacters,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        try
        {
            using (var bounded = XmlReader.Create(new StringReader(normalized), settings))
            {
                int nodes = 0;
                while (bounded.Read())
                    if (++nodes > MaximumTableNodes || bounded.Depth > MaximumTableDepth)
                        return false;
            }
            using var reader = XmlReader.Create(new StringReader(normalized), settings);
            table = XElement.Load(reader);
            return table.Name == "table" && !table.Attributes().Any(attribute => attribute.IsNamespaceDeclaration);
        }
        catch (XmlException) { return false; }
    }

    private static bool ExtractTables(string html, CancellationToken ct, out string? info, out string? files)
    {
        info = files = null;
        int cursor = 0, depth = 0, start = -1, role = 0, tags = 0;
        while ((cursor = html.IndexOf('<', cursor)) >= 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++tags > 32768)
                return false;
            if (html.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = html.IndexOf("-->", cursor + 4, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                cursor = end + 3;
                continue;
            }
            if (html.AsSpan(cursor).StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
            { cursor += "<!DOCTYPE html>".Length; continue; }
            if (!ReadTag(html, cursor, out var tag))
                return false;
            // These unsupported grammars cannot be selected safely by this closed-layout extractor.
            if (tag.Name is "plaintext" or "svg" or "math")
                return false;
            if (RawTextTags.Contains(tag.Name))
            {
                if (tag.Closing || !SkipRawText(html, tag, ct, ref tags, out cursor))
                    return false;
                continue;
            }
            if (tag.Name is "template" or "noscript")
            {
                if (tag.Closing || !SkipInertContent(html, tag, ct, ref tags, out cursor))
                    return false;
                continue;
            }
            if (tag.Name == "table")
            {
                if (!tag.Closing)
                {
                    if (tag.Empty || ++depth > MaximumTableDepth || !TableRole(tag.Attributes, out int selected))
                        return false;
                    if (selected != 0)
                    {
                        if (depth != 1 || start >= 0 || selected == 1 && info is not null || selected == 2 && files is not null)
                            return false;
                        start = cursor;
                        role = selected;
                    }
                }
                else
                {
                    if (depth <= 0)
                        return false;
                    if (depth == 1 && start >= 0)
                    {
                        string fragment = html[start..tag.End];
                        if (role == 1)
                            info = fragment;
                        else
                            files = fragment;
                        start = -1;
                        role = 0;
                    }
                    depth--;
                }
            }
            cursor = tag.End;
        }
        return depth == 0 && info is not null && files is not null;
    }

    private static bool SkipRawText(string html, Tag opening, CancellationToken ct, ref int tags, out int cursor)
    {
        cursor = opening.End;
        if (opening.Empty)
            return false;
        string delimiter = "</" + opening.Name;
        while ((cursor = html.IndexOf(delimiter, cursor, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++tags > 32768)
                return false;
            if (ReadTag(html, cursor, out var closed) && closed.Closing && closed.Name == opening.Name)
            { cursor = closed.End; return true; }
            cursor += 2;
        }
        return false;
    }

    private static bool SkipInertContent(string html, Tag opening, CancellationToken ct, ref int tags, out int cursor)
    {
        cursor = opening.End;
        if (opening.Empty)
            return false;
        var contexts = new Stack<string>();
        contexts.Push(opening.Name);
        while ((cursor = html.IndexOf('<', cursor)) >= 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++tags > 32768)
                return false;
            if (html.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = html.IndexOf("-->", cursor + 4, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                cursor = end + 3;
                continue;
            }
            if (!ReadTag(html, cursor, out var tag) || tag.Name is "plaintext" or "svg" or "math")
                return false;
            if (RawTextTags.Contains(tag.Name))
            {
                if (tag.Closing || !SkipRawText(html, tag, ct, ref tags, out cursor))
                    return false;
                continue;
            }
            if (tag.Name is "template" or "noscript")
            {
                if (tag.Empty)
                    return false;
                if (tag.Closing)
                {
                    if (contexts.Peek() != tag.Name)
                        return false;
                    contexts.Pop();
                    if (contexts.Count == 0)
                    { cursor = tag.End; return true; }
                }
                else
                {
                    if (contexts.Count >= MaximumTableDepth)
                        return false;
                    contexts.Push(tag.Name);
                }
            }
            cursor = tag.End;
        }
        return false;
    }

    private readonly record struct Tag(string Name, string Attributes, bool Closing, bool Empty, int End);

    private static bool ReadTag(string text, int start, out Tag tag)
    {
        tag = default;
        int index = start + 1;
        bool closing = index < text.Length && text[index] == '/';
        if (closing)
            index++;
        int nameStart = index;
        while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '-' or ':'))
            index++;
        if (index == nameStart || !char.IsAsciiLetter(text[nameStart]))
            return false;
        string name = text[nameStart..index].ToLowerInvariant();
        int attributesStart = index;
        char quote = '\0';
        for (; index < text.Length; index++)
        {
            char value = text[index];
            if (quote != '\0')
            { if (value == quote) quote = '\0'; continue; }
            if (value is '\'' or '"')
            { quote = value; continue; }
            if (value == '<')
                return false;
            if (value != '>')
                continue;
            string attributes = text[attributesStart..index].Trim();
            bool empty = attributes.EndsWith('/');
            if (empty)
                attributes = attributes[..^1].TrimEnd();
            if (closing && (empty || attributes.Length != 0))
                return false;
            tag = new(name, attributes, closing, empty, index + 1);
            return true;
        }
        return false;
    }

    private static bool TableRole(string attributes, out int role)
    {
        role = 0;
        int cursor = 0;
        string? classes = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (cursor < attributes.Length)
        {
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
                cursor++;
            if (cursor == attributes.Length)
                break;
            int start = cursor;
            while (cursor < attributes.Length && (char.IsAsciiLetterOrDigit(attributes[cursor]) || attributes[cursor] is '-' or '_' or ':'))
                cursor++;
            if (cursor == start || !names.Add(attributes[start..cursor]))
                return false;
            string name = attributes[start..cursor];
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
                cursor++;
            string value = "";
            if (cursor < attributes.Length && attributes[cursor] == '=')
            {
                cursor++;
                while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
                    cursor++;
                if (cursor == attributes.Length || attributes[cursor] is not ('\'' or '"'))
                    return false;
                char quote = attributes[cursor++];
                start = cursor;
                while (cursor < attributes.Length && attributes[cursor] != quote)
                    cursor++;
                if (cursor == attributes.Length)
                    return false;
                value = attributes[start..cursor++];
                if (cursor < attributes.Length && !char.IsWhiteSpace(attributes[cursor]))
                    return false;
            }
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
                classes = value;
        }
        var tokens = classes?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        bool info = tokens.Contains("disc-info-table", StringComparer.Ordinal), files = tokens.Contains("files-table", StringComparer.Ordinal);
        if (info && files)
            return false;
        role = info ? 1 : files ? 2 : 0;
        return true;
    }

    private static bool NormalizeTags(string fragment, out string normalized)
    {
        normalized = "";
        var output = new StringBuilder(fragment.Length);
        int cursor = 0;
        while (cursor < fragment.Length)
        {
            int start = fragment.IndexOf('<', cursor);
            if (start < 0)
            { output.Append(fragment.AsSpan(cursor)); break; }
            output.Append(fragment.AsSpan(cursor, start - cursor));
            if (fragment.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = fragment.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                output.Append(fragment.AsSpan(start, end + 3 - start));
                cursor = end + 3;
                continue;
            }
            if (!ReadTag(fragment, start, out var tag))
                return false;
            output.Append('<');
            if (tag.Closing)
                output.Append('/');
            output.Append(tag.Name);
            if (tag.Attributes.Length != 0)
                output.Append(' ').Append(tag.Attributes);
            if (tag.Empty || !tag.Closing && VoidTags.Contains(tag.Name))
                output.Append('/');
            output.Append('>');
            cursor = tag.End;
        }
        normalized = output.ToString();
        return true;
    }
}

public static class RedumpDiscMediaReferenceErrors
{
    public static Error InvalidContext() => Error.Validation("RedumpDiscReference.Context", "Invalid canonical source identity, UTC retrieval or retained provenance.");
    public static Error InputLimit() => Error.Validation("RedumpDiscReference.Limit", "The reference body exceeds its bounded nonempty input limit.");
    public static Error InvalidUtf8() => Error.Validation("RedumpDiscReference.Utf8", "The reference body is not valid UTF8.");
    public static Error Malformed() => Error.Validation("RedumpDiscReference.Malformed", "The reference has missing, ambiguous or malformed singleton DVD facts.");
    public static Error Unsupported() => Error.Validation("RedumpDiscReference.Unsupported", "Only explicit Sony PlayStation 2 singleton DVD references are supported.");
}
