using Serilog.Core;
using Serilog.Events;

namespace Romd.Hosting;

/// <summary>
///     Keeps capability tokens out of every log event both API hosts write
///     (docs/decisions/api-contract-policy.md, "Protected By Default"). The request path reaches
///     events through the request log, ASP.NET Core's request starting/finished events and the
///     hosting log scope that every in-request event carries, so the path properties are rewritten
///     here rather than at each source. A capability route logs as <c>{route}/[redacted]</c>.
///     Query strings can carry credentials too (the SignalR <c>access_token</c>, the device-flow
///     <c>user_code</c>, the OAuth callback <c>code</c>), so any query string logs as
///     <c>?[redacted]</c>.
/// </summary>
public sealed class RomdLogRedactionEnricher : ILogEventEnricher
{
    private const string Redacted = "[redacted]";

    /// <summary>Routes whose remaining path is a bearer capability token.</summary>
    private static readonly string[] CapabilityTokenRoutes =
    [
        "/delivery/content/",
        "/delivery/bios/",
        "/delivery/original-layout/",
        "/delivery/derived-descriptor/",
        "/api/download-tickets/"
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        RedactCapabilityToken(logEvent, "RequestPath");
        RedactCapabilityToken(logEvent, "Path");
        if (logEvent.Properties.TryGetValue("QueryString", out var query) && query is ScalarValue { Value: string { Length: > 0 } })
            logEvent.AddOrUpdateProperty(new LogEventProperty("QueryString", new ScalarValue("?" + Redacted)));
    }

    private static void RedactCapabilityToken(LogEvent logEvent, string propertyName)
    {
        if (logEvent.Properties.TryGetValue(propertyName, out var value)
            && value is ScalarValue { Value: string path }
            && CapabilityTokenRoutes.FirstOrDefault(route => path.StartsWith(route, StringComparison.OrdinalIgnoreCase)) is { } route)
            logEvent.AddOrUpdateProperty(new LogEventProperty(propertyName, new ScalarValue(route + Redacted)));
    }
}
