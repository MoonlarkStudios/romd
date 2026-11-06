using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class NginxIngressStreamingTests
{
    [NginxFact]
    public Task HttpsTemplate_SlowVfsRead_AppliesBackpressureWithoutTemporaryFiles() => RunAsync(tls: true);

    [NginxFact]
    public Task TunnelTemplate_SlowVfsRead_AppliesBackpressureWithoutTemporaryFiles() => RunAsync(tls: false);

    private static async Task RunAsync(bool tls)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        // Larger than socket buffers: the buffered control must spill to disk,
        // while a VFS response must leave the upstream waiting for its reader.
        byte[] bytes = new byte[32 * 1024 * 1024];
        new Random(1729).NextBytes(bytes);
        byte[] expectedHash = SHA256.HashData(bytes);
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"streaming-probe\"");
        var lastModified = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var bufferedComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.Run(async context =>
        {
            if (context.Request.Headers.Authorization != "Bearer ingress-probe")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Response.Headers["X-Probe-Raw-Target"] = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
            context.Response.Headers["X-Probe-Forwarded-Host"] = context.Request.Headers["X-Forwarded-Host"];
            context.Response.Headers["X-Probe-Forwarded-Proto"] = context.Request.Headers["X-Forwarded-Proto"];
            context.Response.Headers["X-Probe-Forwarded-For"] = context.Request.Headers["X-Forwarded-For"];
            await Results.File(bytes, "application/octet-stream", lastModified: lastModified,
                entityTag: etag, enableRangeProcessing: true).ExecuteAsync(context);
            if (context.Request.Method == "GET" && context.Response.StatusCode == StatusCodes.Status200OK)
                (context.Request.Path.StartsWithSegments("/vfs") ? streamingComplete : bufferedComplete).TrySetResult();
        });
        await upstream.StartAsync(ct);
        var upstreamOrigin = new Uri(upstream.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.Add(certificate);
        var origin = NginxTlsProxy.AllocateOrigin(tls);
        await using var proxy = await NginxTlsProxy.StartAsync(upstreamOrigin, certificate, origin, tls);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = { CertificateChainPolicy = policy },
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        })
        { BaseAddress = origin };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ingress-probe");

        using (var control = await client.GetAsync("/buffered-probe", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            control.StatusCode.ShouldBe(HttpStatusCode.OK);
            await WaitForAsync(() => proxy.TemporaryResponseFileCount > 0, ct);
            await bufferedComplete.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await using var body = await control.Content.ReadAsStreamAsync(ct);
            (await SHA256.HashDataAsync(body, ct)).ShouldBe(expectedHash);
        }
        proxy.TemporaryResponseFileCount.ShouldBe(1);

        const string path = "/vfs/library/track%20100%25%20%231.bin";
        using (var slow = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            slow.StatusCode.ShouldBe(HttpStatusCode.OK);
            slow.Content.Headers.ContentLength.ShouldBe(bytes.Length);
            slow.Headers.ETag!.Tag.ShouldBe(etag.Tag.ToString());
            slow.Content.Headers.LastModified.ShouldBe(lastModified);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            proxy.TemporaryResponseFileCount.ShouldBe(1);
            streamingComplete.Task.IsCompleted.ShouldBeFalse("the paused reader must exert upstream backpressure");
            await using var body = await slow.Content.ReadAsStreamAsync(ct);
            (await SHA256.HashDataAsync(body, ct)).ShouldBe(expectedHash);
        }
        await streamingComplete.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        proxy.TemporaryResponseFileCount.ShouldBe(1);

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        rangeRequest.Headers.Range = new RangeHeaderValue(65530, 69625);
        rangeRequest.Headers.Add("X-Forwarded-Host", "attacker.invalid");
        rangeRequest.Headers.Add("X-Forwarded-Proto", "http");
        rangeRequest.Headers.Add("X-Forwarded-For", "203.0.113.7");
        using var range = await client.SendAsync(rangeRequest, ct);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        range.Content.Headers.ContentRange!.ToString().ShouldBe($"bytes 65530-69625/{bytes.Length}");
        (await range.Content.ReadAsByteArrayAsync(ct)).ShouldBe(bytes[65530..69626]);
        range.Headers.GetValues("X-Probe-Raw-Target").Single().ShouldBe(path);
        range.Headers.GetValues("X-Probe-Forwarded-Host").Single().ShouldBe("localhost");
        range.Headers.GetValues("X-Probe-Forwarded-Proto").Single().ShouldBe("https");
        range.Headers.GetValues("X-Probe-Forwarded-For").Single().ShouldBe("127.0.0.1");

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, path);
        using var head = await client.SendAsync(headRequest, ct);
        head.StatusCode.ShouldBe(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.ShouldBe(bytes.Length);
        (await head.Content.ReadAsByteArrayAsync(ct)).ShouldBeEmpty();
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, path);
        conditionalRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag.ToString()));
        using var conditional = await client.SendAsync(conditionalRequest, ct);
        conditional.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await conditional.Content.ReadAsByteArrayAsync(ct)).ShouldBeEmpty();
        client.DefaultRequestHeaders.Authorization = null;
        using var unauthorized = await client.GetAsync(path, ct);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
}
