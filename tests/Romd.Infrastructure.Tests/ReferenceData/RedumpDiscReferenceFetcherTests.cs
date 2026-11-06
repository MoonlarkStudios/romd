using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ErrorOr;
using Romd.Infrastructure.ReferenceData;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

/// <summary>Synthetic transport controls only; these tests perform no HTTPS retrieval.</summary>
public sealed class RedumpDiscReferenceFetcherTests
{
    private const int ExpectedBodyLimit = 1 << 20;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><body>synthetic € 😀</body></html>");

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public async Task FetchAsync_PositiveId_ConstructsExactCanonicalGetAndCompleteContext(int id)
    {
        var clock = new DeadlineClock(Now);
        var stream = new TrackedStream(Html, 2);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, clock).FetchAsync(id);
        result.IsError.ShouldBeFalse();
        string expected = "https://redump.info/disc/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        handler.RequestUri.ShouldBe(expected);
        handler.RequestMethod.ShouldBe("GET");
        handler.Accept.ShouldBe("text/html");
        handler.Calls.ShouldBe(1);
        result.Value.DiscId.ShouldBe(id);
        result.Value.CanonicalSourceUrl.ShouldBe(expected);
        result.Value.RetrievedAtUtc.ShouldBe(Now);
        result.Value.RetrievedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        result.Value.AcceptedSourceProvenance.ShouldBe("redump-canonical-https-disc/v1");
        result.Value.RawBodyBytes.ShouldBe(Html.Length);
        result.Value.CopyRawBody().ShouldBe(Html);
        clock.Timers.Single().DueTime.ShouldBe(TimeSpan.FromSeconds(30));
        clock.Timers.Single().Period.ShouldBe(Timeout.InfiniteTimeSpan);
        clock.Timers.Single().Disposed.ShouldBeTrue();
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_RawCopiesAndCompletionTime_RetainsImmutableCompleteBody()
    {
        var clock = new DeadlineClock(Now);
        var stream = new TrackedStream(Html, 3) { BeforeRead = s => { if (s.BytesRead == Html.Length) clock.SetUtcNow(Now.AddHours(1).ToOffset(TimeSpan.FromHours(2))); } };
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, clock).FetchAsync(42);
        result.IsError.ShouldBeFalse();
        byte[] copy = result.Value.CopyRawBody();
        copy[0] ^= 0xff;
        result.Value.CopyRawBody().ShouldBe(Html);
        result.Value.RetrievedAtUtc.ShouldBe(Now.AddHours(1));
        result.Value.RetrievedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task FetchAsync_NonpositiveId_RejectsBeforeSending(int id)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send"));
        using var client = Client(handler);
        var clock = new DeadlineClock(Now);
        (await new RedumpDiscReferenceFetcher(client, clock).FetchAsync(id)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidDiscId());
        handler.Calls.ShouldBe(0);
        clock.Timers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(204)]
    [InlineData(206)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task FetchAsync_RefusedStatus_DoesNotReadOrFollowResponse(int status)
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.StatusCode = (HttpStatusCode)status;
            response.Headers.Location = new Uri("https://other.invalid/disc/42");
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.RefusedStatus());
        handler.Calls.ShouldBe(1);
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task FetchAsync_TransientStatus_RemainsPendingWithoutBody(int status)
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.StatusCode = (HttpStatusCode)status;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.Unavailable());
        result.FirstError.Type.ShouldBe(ErrorType.Failure);
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://redump.info/disc/42")]
    [InlineData("https://REDUMP.info/disc/42")]
    [InlineData("https://www.redump.info/disc/42")]
    [InlineData("https://other.invalid/disc/42")]
    [InlineData("https://redump.info:443/disc/42")]
    [InlineData("https://user@redump.info/disc/42")]
    [InlineData("https://redump.info/disc/43")]
    [InlineData("https://redump.info/disc/042")]
    [InlineData("https://redump.info/disc/42/")]
    [InlineData("https://redump.info/disc/42?source=alias")]
    [InlineData("https://redump.info/disc/42#alias")]
    [InlineData("https://redump.info/disc/%34%32")]
    public async Task FetchAsync_ResponseSourceAlias_RejectsBeforeBody(string? source)
    {
        var stream = new TrackedStream(Html);
        using var sourceRequest = new HttpRequestMessage(HttpMethod.Get, source);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.RequestMessage = sourceRequest;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeTrue();
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ResponseSource());
        handler.RequestUri.ShouldBe("https://redump.info/disc/42");
        handler.Calls.ShouldBe(1);
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_MissingRequestContext_RejectsBeforeBody()
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.RequestMessage = null;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ResponseSource());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_ResponseMethodChanged_RejectsBeforeBody()
    {
        var stream = new TrackedStream(Html);
        using var sourceRequest = new HttpRequestMessage(HttpMethod.Post, "https://redump.info/disc/42");
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.RequestMessage = sourceRequest;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ResponseSource());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("application/xhtml+xml")]
    public async Task FetchAsync_NonHtmlType_RejectsWithoutBody(string? mediaType)
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.Content.Headers.ContentType = mediaType is null ? null : new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ContentType());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("iso-8859-1")]
    [InlineData("utf-16")]
    [InlineData("utf8")]
    public async Task FetchAsync_NonUtf8Charset_RejectsWithoutBody(string charset)
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.Content.Headers.ContentType!.CharSet = charset;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ContentType());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData("\"utf-8\"")]
    public async Task FetchAsync_HtmlUtf8CharsetOrAbsent_AcceptsCompleteStrictBytes(string? charset)
    {
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, new TrackedStream(Html, 1));
            response.Content.Headers.ContentType!.CharSet = charset;
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeFalse();
        result.Value.CopyRawBody().ShouldBe(Html);
    }

    [Fact]
    public async Task FetchAsync_ResidualEncodedContent_RejectsBeforeBody()
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidBody());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("plus-sign")]
    [InlineData("minus-sign")]
    [InlineData("comma-combined")]
    [InlineData("non-ascii")]
    [InlineData("overflow")]
    [InlineData("duplicate-equal")]
    [InlineData("duplicate-conflicting")]
    public async Task FetchAsync_RawContentLengthInvalidOrDuplicate_RejectsBeforeBody(string kind)
    {
        string length = Html.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] rawValues = kind switch
        {
            "garbage" => ["not-a-length"],
            "plus-sign" => ["+" + length],
            "minus-sign" => ["-" + length],
            "comma-combined" => [length + "," + length],
            "non-ascii" => ["\u0664\u0666"],
            "overflow" => ["9223372036854775808"],
            "duplicate-equal" => [length, length],
            "duplicate-conflicting" => [length, (Html.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            _ => throw new InvalidOperationException("Unknown raw header case.")
        };
        var stream = new TrackedStream(Html, 2);
        stream.CanSeek.ShouldBeFalse();
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.Content.Headers.NonValidated.Contains("Content-Length").ShouldBeFalse();
            response.Content.Headers.TryAddWithoutValidation("Content-Length", rawValues).ShouldBeTrue();
            response.Content.Headers.NonValidated.TryGetValues("Content-Length", out var observed).ShouldBeTrue();
            observed.ToArray().ShouldBe(rawValues);
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeTrue();
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidBody());
        handler.Calls.ShouldBe(1);
        stream.ReadCalls.ShouldBe(0);
        stream.BytesRead.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_RawContentLengthOwsOrLeadingZeros_AcceptsCompleteBody(bool leadingZeros)
    {
        string length = Html.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rawValue = leadingZeros ? length.PadLeft(19, '0') : " \t" + length + "\t ";
        var stream = new TrackedStream(Html, 2);
        stream.CanSeek.ShouldBeFalse();
        using var handler = new Handler((request, _) =>
        {
            var response = Response(request, stream);
            response.Content.Headers.NonValidated.Contains("Content-Length").ShouldBeFalse();
            response.Content.Headers.TryAddWithoutValidation("Content-Length", rawValue).ShouldBeTrue();
            response.Content.Headers.NonValidated.TryGetValues("Content-Length", out var observed).ShouldBeTrue();
            observed.ToArray().ShouldBe(new[] { rawValue });
            return Task.FromResult(response);
        });
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeFalse();
        result.Value.CopyRawBody().ShouldBe(Html);
        result.Value.RawBodyBytes.ShouldBe(Html.Length);
        handler.Calls.ShouldBe(1);
        stream.BytesRead.ShouldBe(Html.Length);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_DeclaredAboveFixedLimit_RejectsBeforeReading()
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream, ExpectedBodyLimit + 1)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeTrue();
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InputLimit());
        stream.ReadCalls.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_UnknownLengthStreamingBody_EnforcesExactFixedLimit(bool overflow)
    {
        byte[] raw = Enumerable.Repeat((byte)' ', ExpectedBodyLimit + (overflow ? 1 : 0)).ToArray();
        var stream = new TrackedStream(raw, 37);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        if (overflow)
        {
            result.IsError.ShouldBeTrue();
            result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InputLimit());
        }
        else
        {
            result.IsError.ShouldBeFalse();
            result.Value.CopyRawBody().ShouldBe(raw);
        }
        stream.BytesRead.ShouldBe(raw.Length);
        stream.BytesRead.ShouldBeLessThanOrEqualTo(ExpectedBodyLimit + 1);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_KnownExactLimit_AcceptsCompleteBody()
    {
        byte[] raw = Enumerable.Repeat((byte)' ', ExpectedBodyLimit).ToArray();
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, new TrackedStream(raw), ExpectedBodyLimit)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeFalse();
        result.Value.CopyRawBody().ShouldBe(raw);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task FetchAsync_DeclaredLengthMismatch_RejectsCompleteBody(int delta)
    {
        var stream = new TrackedStream(Html);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream, Html.Length + delta)));
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidBody());
        stream.BytesRead.ShouldBe(Html.Length);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_EmptyBody_DoesNotReturnEnvelope()
    {
        var stream = new TrackedStream([]);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InputLimit());
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("80")]
    [InlineData("C0AF")]
    [InlineData("F09F")]
    [InlineData("EDA080")]
    public async Task FetchAsync_InvalidUtf8_RejectsRawBytes(string hex)
    {
        var stream = new TrackedStream(Convert.FromHexString(hex), 1);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.IsError.ShouldBeTrue();
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidUtf8());
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_NetworkFailure_RemainsPendingWithoutLeakingDetails()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private upstream detail"));
        using var client = Client(handler);
        var result = await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42);
        result.FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.Unavailable());
        result.FirstError.Description.ShouldNotContain("private upstream detail");
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FetchAsync_StreamFailure_DiscardsPartialBodyAndRemainsPending(int afterBytes)
    {
        var stream = new TrackedStream(Html, 2) { FailAfterBytes = afterBytes };
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.Unavailable());
        stream.BytesRead.ShouldBe(afterBytes);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FetchAsync_MalformedStreamData_DiscardsPartialBodyAndRejects(int afterBytes)
    {
        var stream = new TrackedStream(Html, 2) { FailAfterBytes = afterBytes, ReadFailure = new InvalidDataException("malformed encoding") };
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        (await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidBody());
        stream.BytesRead.ShouldBe(afterBytes);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchAsync_UnexpectedHandlerFailure_PropagatesOriginalException()
    {
        var expected = new InvalidOperationException("unexpected handler defect");
        using var handler = new Handler((_, _) => throw expected);
        using var client = Client(handler);
        InvalidOperationException? actual = null;
        try
        { await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42); }
        catch (InvalidOperationException error) { actual = error; }
        actual.ShouldBeSameAs(expected);
    }

    [Fact]
    public async Task FetchAsync_UnrelatedHandlerCancellation_PropagatesOriginalException()
    {
        using var unrelated = new CancellationTokenSource();
        var expected = new OperationCanceledException(unrelated.Token);
        using var handler = new Handler((_, _) => throw expected);
        using var client = Client(handler);
        OperationCanceledException? actual = null;
        try
        { await new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42); }
        catch (OperationCanceledException error) { actual = error; }
        actual.ShouldBeSameAs(expected);
    }

    [Fact]
    public async Task FetchAsync_AlreadyCancelledCaller_DoesNotSendAndPreservesToken()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send"));
        using var client = Client(handler);
        await AssertCallerCancellation(new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42, caller.Token), caller.Token);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task FetchAsync_CallerCancelledDuringBody_DiscardsPartialBodyAndPreservesToken()
    {
        using var caller = new CancellationTokenSource();
        var stream = new TrackedStream(Html, 2) { BeforeRead = s => { if (s.BytesRead == 2) caller.Cancel(); } };
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        await AssertCallerCancellation(new RedumpDiscReferenceFetcher(client, new DeadlineClock(Now)).FetchAsync(42, caller.Token), caller.Token);
        stream.BytesRead.ShouldBe(2);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_StreamFailureAfterCancellation_PreservesCallerOrFixedDeadline(bool deadline)
    {
        using var caller = new CancellationTokenSource();
        var clock = new DeadlineClock(Now);
        var stream = new TrackedStream(Html, 2)
        {
            BeforeRead = s =>
            {
                if (s.BytesRead != 2)
                    return;
                if (deadline)
                    clock.Advance(TimeSpan.FromSeconds(30));
                else
                    caller.Cancel();
                throw new IOException("failure after cancellation");
            }
        };
        using var handler = new Handler((request, _) => Task.FromResult(Response(request, stream)));
        using var client = Client(handler);
        var operation = new RedumpDiscReferenceFetcher(client, clock).FetchAsync(42, caller.Token);
        if (deadline)
            (await operation).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.TimedOut());
        else
            await AssertCallerCancellation(operation, caller.Token);
        stream.BytesRead.ShouldBe(2);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_FixedDeadlineBeforeHeadersOrDuringBody_ExpiresAndDiscardsPartialBody(bool duringBody)
    {
        using var caller = new CancellationTokenSource();
        var clock = new DeadlineClock(Now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TrackedStream(Html, 2) { BlockAfterBytes = duringBody ? 2 : int.MaxValue };
        using var handler = new Handler(async (request, ct) =>
        {
            if (!duringBody)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Response(request, stream);
        });
        using var client = Client(handler);
        Task<ErrorOr<RedumpDiscRawRetrieval>> operation = new RedumpDiscReferenceFetcher(client, clock).FetchAsync(42, caller.Token);
        try
        {
            await (duringBody ? stream.Entered.Task : entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            clock.Timers.Single().DueTime.ShouldBe(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(29));
            operation.IsCompleted.ShouldBeFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            (await operation.WaitAsync(TimeSpan.FromSeconds(5))).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.TimedOut());
            handler.Calls.ShouldBe(1);
            if (duringBody)
            {
                stream.BytesRead.ShouldBe(2);
                stream.Disposed.ShouldBeTrue();
            }
            clock.Timers.Single().Disposed.ShouldBeTrue();
        }
        finally
        {
            caller.Cancel();
            try
            { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task AssertCallerCancellation(Task<ErrorOr<RedumpDiscRawRetrieval>> operation, CancellationToken expected)
    {
        OperationCanceledException? actual = null;
        try
        { await operation; }
        catch (OperationCanceledException error) { actual = error; }
        if (actual is null)
            throw new InvalidOperationException("Expected caller cancellation.");
        actual.CancellationToken.ShouldBe(expected);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, false) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Response(HttpRequestMessage request, TrackedStream stream, long? declared = null)
    {
        var content = new RawStreamContent(stream, declared);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestUri { get; private set; }
        public string? RequestMethod { get; private set; }
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri?.OriginalString;
            RequestMethod = request.Method.Method;
            Accept = request.Headers.Accept.ToString();
            return send(request, cancellationToken);
        }
    }

    private sealed class RawStreamContent(TrackedStream stream, long? declared) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = declared.GetValueOrDefault(); return declared.HasValue; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new InvalidOperationException("fetch must stream without buffering");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(stream); }
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TrackedStream(byte[] bytes, int chunk = int.MaxValue) : Stream
    {
        public int BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public bool Disposed { get; private set; }
        public int FailAfterBytes { get; init; } = int.MaxValue;
        public Exception? ReadFailure { get; init; }
        public int BlockAfterBytes { get; init; } = int.MaxValue;
        public Action<TrackedStream>? BeforeRead { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("synchronous read is forbidden");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadCalls++;
            ct.ThrowIfCancellationRequested();
            BeforeRead?.Invoke(this);
            ct.ThrowIfCancellationRequested();
            if (BytesRead >= FailAfterBytes)
                throw ReadFailure ?? new IOException("private stream detail");
            if (BytesRead >= BlockAfterBytes)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            int read = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, read).CopyTo(buffer);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DeadlineClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public List<DeadlineTimer> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset value) => _now = value;
        public void Advance(TimeSpan value)
        {
            _now += value;
            foreach (var timer in Timers.ToArray())
                timer.FireIfDue(_now);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private DateTimeOffset _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan delay, TimeSpan interval)
        {
            if (Disposed)
                return false;
            DueTime = delay;
            Period = interval;
            _dueAt = delay == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + delay;
            return true;
        }

        public void FireIfDue(DateTimeOffset value)
        {
            if (Disposed || value < _dueAt)
                return;
            _dueAt = DateTimeOffset.MaxValue;
            callback(state);
        }

        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
