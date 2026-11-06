using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Romd.Domain.Hashing;

namespace Romd.Media.Probe;

/// <summary>One independently checked HTTP range, timed across all admission attempts and backoff.</summary>
internal static class InteractiveRangeRead
{
    private const int _maximumAttempts = 128;
    internal sealed record Range(string Url, long FileBytes, Sha256 Sha256, long Offset, int RequestedBytes, byte[] Expected);
    internal sealed record Attempt(int Number, int? Status, string Outcome, double? HeadersMilliseconds,
        double ElapsedMilliseconds, double ScheduledBackoffMilliseconds, double ObservedBackoffMilliseconds);
    internal sealed record Measurement(string Outcome, int Attempts, int Rejections, double BackoffMilliseconds,
        double? FirstByteMilliseconds, double ElapsedMilliseconds, long Bytes, string? Error, IReadOnlyList<Attempt> Trace);

    internal static async Task ValidateRejectionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable || delay is null || delay <= TimeSpan.Zero || delay > TimeSpan.FromSeconds(15) ||
            response.Headers.ETag is not null || response.Content.Headers.ContentRange is not null || response.Content.Headers.ContentLength is > 0)
            throw new InvalidDataException("Invalid bodyless capacity rejection or Retry-After.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        byte[] sentinel = new byte[1];
        if (await body.ReadAsync(sentinel, ct) != 0)
            throw new InvalidDataException("Capacity rejection carried a body.");
    }

    internal static async Task<Measurement> ReadAsync(HttpClient client, Range range, TimeSpan deadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(range);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(2) || range.FileBytes <= 0 || range.Sha256.IsEmpty ||
            range.Offset < 0 || range.Offset >= range.FileBytes || range.RequestedBytes is < 1 or > 1 << 20 ||
            range.Expected.Length != Math.Min(range.RequestedBytes, range.FileBytes - range.Offset))
            throw new ArgumentException("Bounded, complete independent range oracle and deadline required.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(deadline);
        var token = cancellation.Token;
        long started = Stopwatch.GetTimestamp();
        double? firstByte = null;
        int attempts = 0, rejections = 0;
        double backoff = 0;
        long received = 0;
        var trace = new List<Attempt>();
        while (attempts < _maximumAttempts)
        {
            long attemptStarted = Stopwatch.GetTimestamp();
            double? headers = null;
            int? status = null;
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, range.Url);
                request.Headers.Range = new RangeHeaderValue(range.Offset, checked(range.Offset + range.RequestedBytes - 1));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                headers = Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds;
                status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await ValidateRejectionAsync(response, token);
                    rejections++;
                    var delay = response.Headers.RetryAfter!.Delta!.Value;
                    double attemptElapsed = Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds;
                    long waitStarted = Stopwatch.GetTimestamp();
                    try
                    { await Task.Delay(delay, token); }
                    finally
                    {
                        double observed = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                        backoff += observed;
                        trace.Add(new(attempts, status, "capacity-rejected", headers, attemptElapsed, delay.TotalMilliseconds, observed));
                    }
                    continue;
                }
                var contentRange = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || response.Headers.ETag?.ToString() != "\"" + range.Sha256 + "\"" ||
                    contentRange?.Unit != "bytes" || contentRange.From != range.Offset || contentRange.To != range.Offset + range.Expected.Length - 1 ||
                    contentRange.Length != range.FileBytes || response.Content.Headers.ContentLength != range.Expected.Length)
                    throw new InvalidDataException("Successful range headers disagree with the exact independent original.");
                await using var body = await response.Content.ReadAsStreamAsync(token);
                byte[] buffer = new byte[Math.Min(64 << 10, range.Expected.Length)];
                int count;
                while ((count = await body.ReadAsync(buffer.AsMemory(0, firstByte is null ? 1 : buffer.Length), token)) != 0)
                {
                    firstByte ??= Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (received + count > range.Expected.Length || !buffer.AsSpan(0, count).SequenceEqual(range.Expected.AsSpan((int)received, count)))
                        throw new InvalidDataException("HTTP range bytes disagree with the independently authenticated original.");
                    received += count;
                }
                if (received != range.Expected.Length)
                    throw new InvalidDataException("Truncated HTTP range body.");
                trace.Add(new(attempts, status, "completed", headers, Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds, 0, 0));
                return Finish("completed", null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && cancellation.IsCancellationRequested)
            {
                if (trace.Count < attempts)
                    trace.Add(new(attempts, status, "deadline", headers, Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds, 0, 0));
                return Finish("deadline", "The explicitly bounded operation deadline elapsed.");
            }
            catch (HttpRequestException error)
            {
                trace.Add(new(attempts, status, "transport-failure", headers, Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds, 0, 0));
                return Finish("transport-failure", error.GetType().Name);
            }
        }
        return Finish("retry-limit", "The 128-attempt safety bound was reached.");

        Measurement Finish(string outcome, string? error) => new(outcome, attempts, rejections, backoff, firstByte,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, received, error, trace.ToArray());
    }
}
