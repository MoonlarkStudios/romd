using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ErrorOr;

namespace Romd.Infrastructure.ReferenceData;

/// <summary>Fetches one bounded raw Redump disc page outside the supervised media lease.</summary>
/// <remarks>Production composition must supply a client with standard TLS validation,
/// redirects disabled and no retry/fallback. Exact final URI checks alone cannot prove
/// that an arbitrary injected handler followed those rules. Parsing and source acceptance
/// are separate; synthetic handlers do not establish an authenticated HTTPS retrieval.</remarks>
public sealed class RedumpDiscReferenceFetcher
{
    public const string Profile = "redump-canonical-https-disc/v1";
    private const int MaximumBodyBytes = 1 << 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public RedumpDiscReferenceFetcher(HttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        _http = http;
        _time = time;
    }

    public async Task<ErrorOr<RedumpDiscRawRetrieval>> FetchAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (discId <= 0)
            return RedumpDiscReferenceFetchErrors.InvalidDiscId();
        string canonical = "https://redump.info/disc/" + discId.ToString(CultureInfo.InvariantCulture);
        using var deadline = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, canonical);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (response.RequestMessage?.Method.Method != "GET" ||
                response.RequestMessage?.RequestUri?.OriginalString != canonical)
                return RedumpDiscReferenceFetchErrors.ResponseSource();
            if (response.StatusCode != HttpStatusCode.OK)
                return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode is >= 500 and <= 599
                    ? RedumpDiscReferenceFetchErrors.Unavailable()
                    : RedumpDiscReferenceFetchErrors.RefusedStatus();

            MediaTypeHeaderValue? contentType;
            long? contentLength;
            try
            {
                contentType = response.Content.Headers.ContentType;
                if (!TryReadContentLength(response.Content.Headers, out contentLength))
                    return RedumpDiscReferenceFetchErrors.InvalidBody();
            }
            catch (FormatException) { return RedumpDiscReferenceFetchErrors.InvalidBody(); }
            if (!string.Equals(contentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                contentType?.CharSet is { } charset &&
                    !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(charset, "\"utf-8\"", StringComparison.OrdinalIgnoreCase))
                return RedumpDiscReferenceFetchErrors.ContentType();
            // A handler may decode content automatically. Never interpret a residual
            // encoded payload as raw UTF-8; the same decoded representation is retained.
            if (response.Content.Headers.ContentEncoding.Count != 0)
                return RedumpDiscReferenceFetchErrors.InvalidBody();
            if (contentLength is < 0 or > MaximumBodyBytes)
                return RedumpDiscReferenceFetchErrors.InputLimit();

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                int remaining = MaximumBodyBytes - checked((int)body.Length);
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (read == 0)
                    break;
                if (read > remaining)
                    return RedumpDiscReferenceFetchErrors.InputLimit();
                body.Write(buffer, 0, read);
            }
            if (body.Length == 0)
                return RedumpDiscReferenceFetchErrors.InputLimit();
            if (contentLength is { } declared && declared != body.Length)
                return RedumpDiscReferenceFetchErrors.InvalidBody();
            byte[] rawBody = body.ToArray();
            try
            { _ = StrictUtf8.GetCharCount(rawBody); }
            catch (DecoderFallbackException) { return RedumpDiscReferenceFetchErrors.InvalidUtf8(); }
            linked.Token.ThrowIfCancellationRequested();
            return new RedumpDiscRawRetrieval(discId, canonical, _time.GetUtcNow().ToUniversalTime(), rawBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return RedumpDiscReferenceFetchErrors.TimedOut();
        }
        catch (InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested
                ? RedumpDiscReferenceFetchErrors.TimedOut()
                : RedumpDiscReferenceFetchErrors.InvalidBody();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested
                ? RedumpDiscReferenceFetchErrors.TimedOut()
                : RedumpDiscReferenceFetchErrors.Unavailable();
        }
    }

    private static bool TryReadContentLength(HttpContentHeaders headers, out long? contentLength)
    {
        contentLength = null;
        // Inspect raw values before the typed getter can discard invalid syntax or
        // compute an absent value. This retrieval profile requires a single field.
        if (!headers.NonValidated.TryGetValues("Content-Length", out var rawValues))
        {
            contentLength = headers.ContentLength;
            return true;
        }
        using var values = rawValues.GetEnumerator();
        if (!values.MoveNext())
            return false;
        string raw = values.Current;
        if (values.MoveNext() || raw.Length > 64)
            return false;
        int first = 0;
        int end = raw.Length;
        while (first < end && raw[first] is ' ' or '\t')
            first++;
        while (end > first && raw[end - 1] is ' ' or '\t')
            end--;
        ReadOnlySpan<char> digits = raw.AsSpan(first, end - first);
        if (digits.Length is < 1 or > 19)
            return false;
        foreach (char digit in digits)
            if (digit is < '0' or > '9')
                return false;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            return false;
        contentLength = parsed;
        return true;
    }
}
