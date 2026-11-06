using System.Net;
using Romd.Admin.Application.Source.Rom;

namespace Romd.Infrastructure.ReferenceData;

/// <summary>The supported production composition for canonical Redump HTTPS imports.</summary>
/// <remarks>Standard platform TLS validation, no redirects, decoding, retry or alternative source.
/// Construction alone does not qualify live HTTPS authentication; actual retrieval still needs execution.</remarks>
public static class RedumpDiscReferenceImportComposition
{
    public static RedumpDiscReferenceImporter Create(IRomDiscMediaReferenceImportStore imports,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(imports);
        var client = CreateClient();
        try
        { return new(new RedumpDiscReferenceFetcher(client, time ?? TimeProvider.System), imports, client); }
        catch { client.Dispose(); throw; }
    }

    internal static HttpClient CreateClient() => new(CreateTransport(), disposeHandler: true)
    { Timeout = Timeout.InfiniteTimeSpan };

    internal static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16
        // SslOptions retains standard platform defaults; no certificate callbacks are installed.
    };
}
