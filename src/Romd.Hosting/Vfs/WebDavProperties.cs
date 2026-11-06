using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Romd.Hosting.Vfs;

public sealed record WebDavResource(string Href, string Name, long? Length = null, string? ETag = null,
    DateTimeOffset? ModifiedAt = null, string? Md5 = null, string? Sha1 = null);

public sealed class WebDavProperties
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace OwnCloud = "http://owncloud.org/ns";
    private readonly IReadOnlyList<XName>? _requested;
    private readonly bool _namesOnly;
    private readonly IReadOnlyList<XName> _included;
    private WebDavProperties(IReadOnlyList<XName>? requested, bool namesOnly, IReadOnlyList<XName>? included = null)
    {
        _requested = requested;
        _namesOnly = namesOnly;
        _included = included ?? [];
    }

    public static async Task<WebDavProperties> ReadAsync(Stream body, CancellationToken ct)
    {
        byte[] bytes = new byte[16 * 1024 + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = await body.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0)
                break;
            length += read;
        }

        if (length == bytes.Length)
            throw new InvalidDataException("PROPFIND body exceeds 16 KiB.");
        if (length == 0)
            return new(null, false);
        using var source = new MemoryStream(bytes, 0, length);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024
        });
        XElement root = XElement.Load(reader);
        if (root.Name != Dav + "propfind")
            throw new XmlException("Expected DAV:propfind.");
        var modes = root.Elements().Where(element => element.Name == Dav + "prop" ||
            element.Name == Dav + "allprop" || element.Name == Dav + "propname").ToArray();
        if (modes.Length != 1)
            throw new XmlException("Expected one property selection mode.");
        if (modes[0].Name == Dav + "propname")
            return new(null, true);
        if (modes[0].Name == Dav + "allprop")
            return new(null, false, root.Elements(Dav + "include").Elements().Select(element => element.Name).Distinct().ToArray());
        return new(modes[0].Elements().Select(element => element.Name).Distinct().ToArray(), false);
    }

    public async Task WriteAsync(HttpResponse response, IAsyncEnumerable<WebDavResource> resources, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status207MultiStatus;
        response.ContentType = "application/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(false),
            CloseOutput = false
        });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync("D", "multistatus", Dav.NamespaceName);
        await foreach (WebDavResource resource in resources.WithCancellation(ct))
        {
            var values = new Dictionary<XName, object?>
            {
                [Dav + "displayname"] = resource.Name,
                [Dav + "resourcetype"] = resource.Length is null ? new XElement(Dav + "collection") : null,
                [Dav + "getcontenttype"] = resource.Length is null ? "httpd/unix-directory" : "application/octet-stream"
            };
            if (resource.Length is { } length)
                values[Dav + "getcontentlength"] = length.ToString(CultureInfo.InvariantCulture);
            if (resource.ETag is { } etag)
                values[Dav + "getetag"] = etag;
            if (resource.ModifiedAt is { } modified)
                values[Dav + "getlastmodified"] = modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
            if (resource.Md5 is not null && resource.Sha1 is not null)
                values[OwnCloud + "checksums"] = new XElement(OwnCloud + "checksum",
                    $"MD5:{resource.Md5} SHA1:{resource.Sha1}");
            var ok = new XElement(Dav + "prop");
            var missing = new XElement(Dav + "prop");
            foreach (XName name in _requested ?? values.Keys.Concat(_included).Distinct().ToArray())
            {
                if (values.TryGetValue(name, out object? value))
                    ok.Add(new XElement(name, _namesOnly ? null : value));
                else
                    missing.Add(new XElement(name));
            }

            var result = new XElement(Dav + "response", new XElement(Dav + "href", resource.Href));
            if (ok.HasElements || !missing.HasElements)
                result.Add(Propstat(ok, "200 OK"));
            if (missing.HasElements)
                result.Add(Propstat(missing, "404 Not Found"));
            await result.WriteToAsync(writer, ct);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static XElement Propstat(XElement properties, string status) =>
        new(Dav + "propstat", properties, new XElement(Dav + "status", "HTTP/1.1 " + status));
}
