using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Romd.Domain.Hashing;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class InteractiveRangeReadTests
{
    private static readonly byte[] _bytes = [10, 20, 30, 40];
    private static readonly Sha256 _hash = Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(_bytes)));
    private static readonly InteractiveRangeRead.Range _range = new("https://localhost/vfs/file", 100, _hash, 96, 4, _bytes);

    [Fact]
    public async Task ReadAsync_AdmissionRetryAndDelayedBody_IncludesBackoffAndFirstByteWait()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            request.Headers.Range.ShouldBe(new RangeHeaderValue(96, 99));
            if (Interlocked.Increment(ref requests) == 1)
                return new(HttpStatusCode.ServiceUnavailable)
                {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(100)) },
                    Content = new ByteArrayContent([])
                };
            await Task.Delay(30, token);
            return Success(new DelayedStream(_bytes, TimeSpan.FromMilliseconds(70)));
        }));

        var result = await InteractiveRangeRead.ReadAsync(client, _range, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Outcome.ShouldBe("completed");
        result.Attempts.ShouldBe(2);
        result.Rejections.ShouldBe(1);
        result.BackoffMilliseconds.ShouldBeGreaterThanOrEqualTo(80);
        result.Trace[0].Status.ShouldBe(503);
        result.Trace[0].ScheduledBackoffMilliseconds.ShouldBe(100);
        result.Trace[0].ObservedBackoffMilliseconds.ShouldBeGreaterThanOrEqualTo(80);
        result.Trace[1].Status.ShouldBe(206);
        result.FirstByteMilliseconds.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(170);
        result.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(result.FirstByteMilliseconds.Value);
        result.Bytes.ShouldBe(4);
        result.Trace.Count.ShouldBe(result.Attempts);
    }

    [Fact]
    public async Task ReadAsync_ClippedEof_SendsRequestedRangeAndChecksActualClippedBody()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            request.Headers.Range.ShouldBe(new RangeHeaderValue(96, 103));
            return Task.FromResult(Success(new MemoryStream(_bytes)));
        }));

        var result = await InteractiveRangeRead.ReadAsync(client, _range with { RequestedBytes = 8 }, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Outcome.ShouldBe("completed");
        result.Bytes.ShouldBe(4);
    }

    [Fact]
    public async Task ReadAsync_BodyDeadline_RetainsFailureInsteadOfReportingSuccessfulLatency()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Success(new DelayedStream(_bytes, TimeSpan.FromSeconds(10))))));

        var result = await InteractiveRangeRead.ReadAsync(client, _range, TimeSpan.FromMilliseconds(80), CancellationToken.None);

        result.Outcome.ShouldBe("deadline");
        result.Attempts.ShouldBe(1);
        result.FirstByteMilliseconds.ShouldBeNull();
        result.Bytes.ShouldBe(0);
        result.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(60);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("etag")]
    [InlineData("range")]
    [InlineData("extra")]
    public async Task ReadAsync_InvalidSuccessfulResponse_FailsIntegrity(string fault)
    {
        var response = Success(new MemoryStream(fault == "bytes" ? [1, 2, 3, 4] : fault == "extra" ? [10, 20, 30, 40, 50] : _bytes));
        if (fault == "etag")
            response.Headers.ETag = new EntityTagHeaderValue("\"wrong\"");
        if (fault == "range")
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(95, 98, 100);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response)));

        await Should.ThrowAsync<InvalidDataException>(() => InteractiveRangeRead.ReadAsync(client, _range, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectionWithBodyOrFileValidators_FailsInsteadOfRetrying()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ByteArrayContent([1]),
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)), ETag = new EntityTagHeaderValue("\"invalid\"") }
        };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response)));

        await Should.ThrowAsync<InvalidDataException>(() => InteractiveRangeRead.ReadAsync(client, _range, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static HttpResponseMessage Success(Stream body) => new(HttpStatusCode.PartialContent)
    {
        Headers = { ETag = new EntityTagHeaderValue("\"" + _hash + "\"") },
        Content = new StreamContent(body)
        {
            Headers = { ContentRange = new ContentRangeHeaderValue(96, 99, 100), ContentLength = 4 }
        }
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class DelayedStream(byte[] bytes, TimeSpan delay) : MemoryStream(bytes)
    {
        private bool _delayed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
