using System.Net;
using ErrorOr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Endpoint-level contract of <c>GET /media/{mediaId}</c> over a substituted application query:
///     route metadata, HTTP mapping, and disposal of the opened stream on completion, failure, and
///     client cancellation.
/// </summary>
public sealed class StorageEndpointsStreamLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int MediaId = 41;

    private readonly IQueryHandler<OpenTitleMediaQuery, TitleMediaContent> _handler =
        Substitute.For<IQueryHandler<OpenTitleMediaQuery, TitleMediaContent>>();

    [Fact]
    public async Task MapStorageEndpoints_ServeMedia_KeepsRouteNameAnonymousAccessAndOutputCachePolicy()
    {
        await using var app = await StartAsync();

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ShouldHaveSingleItem();

        endpoint.RoutePattern.RawText.ShouldBe("/media/{mediaId}");
        endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods.ShouldBe(["GET"]);
        endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName.ShouldBe("ServeMedia");
        endpoint.Metadata.GetRequiredMetadata<ITagsMetadata>().Tags.ShouldBe(["Storage"]);
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        endpoint.Metadata.GetMetadata<IOutputCachePolicy>().ShouldNotBeNull();
    }

    [Fact]
    public async Task ServeMedia_Found_WritesBytesHeadersAndDisposesStreamOnCompletion()
    {
        var content = new TrackingStream([4, 5, 6]);
        Found(content, "image/png");
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/media/{IdCoder.Encode(MediaId)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([4, 5, 6]);
        var cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
        cacheControl.Public.ShouldBeTrue();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        cacheControl.Extensions.ShouldContain(extension => extension.Name == "immutable");
        await content.Disposed.WaitAsync(Timeout);
        await _handler.Received(1).HandleAsync(new OpenTitleMediaQuery(MediaId), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(NotFoundErrors))]
    public async Task ServeMedia_NotFound_ReturnsEnvelopeWithoutImmutableCaching(string code)
    {
        Error error = code == "Catalog.MediaNotFound"
            ? CatalogErrors.MediaNotFound()
            : CatalogErrors.MediaContentNotFound();
        _handler.HandleAsync(Arg.Any<OpenTitleMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TitleMediaContent>>(error));
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/media/{IdCoder.Encode(MediaId)}");

        using var body = await ProblemEnvelope.ReadAsync(response, 404, "Delivery.ContentNotFound");
        body.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain(MediaId.ToString());
        response.Headers.CacheControl?.Extensions.ShouldNotContain(extension => extension.Name == "immutable");
    }

    public static TheoryData<string> NotFoundErrors() => new() { "Catalog.MediaNotFound", "Catalog.MediaContentNotFound" };

    [Fact]
    public async Task ServeMedia_InvalidSqid_ReturnsBadRequestWithoutOpeningMedia()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/media/not-a-valid-sqid!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task ServeMedia_StreamFailsWhileWriting_DisposesStream()
    {
        var content = new TrackingStream([1], failOnRead: true);
        Found(content, "image/jpeg");
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var response = await client.GetAsync($"/media/{IdCoder.Encode(MediaId)}");
            await response.Content.ReadAsByteArrayAsync();
        });

        await content.Disposed.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ServeMedia_ClientCancelsWhileWriting_DisposesStream()
    {
        var content = new TrackingStream([1], blockOnRead: true);
        Found(content, "image/jpeg");
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var cancellation = new CancellationTokenSource();

        var request = client.GetAsync($"/media/{IdCoder.Encode(MediaId)}", cancellation.Token);
        await content.ReadStarted.WaitAsync(Timeout);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => request);
        await content.Disposed.WaitAsync(Timeout);
    }

    private void Found(Stream content, string contentType) =>
        _handler.HandleAsync(Arg.Any<OpenTitleMediaQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<TitleMediaContent>>(new TitleMediaContent(content, contentType)));

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_handler);
        var app = builder.Build();
        app.MapStorageEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>A non-seekable stream that records disposal and can fail or block while being read.</summary>
    private sealed class TrackingStream(byte[] bytes, bool failOnRead = false, bool blockOnRead = false) : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public Task Disposed => _disposed.Task;
        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            if (failOnRead)
                throw new IOException("Synthetic storage read failure.");
            if (blockOnRead)
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            int count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return base.DisposeAsync();
        }
    }
}
