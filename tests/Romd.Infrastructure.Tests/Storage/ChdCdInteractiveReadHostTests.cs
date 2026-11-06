using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Domain.Source.Rom;
using Romd.Hosting.Vfs;
using Romd.Media.Probe;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdInteractiveReadHostTests
{
    [Theory]
    [InlineData("content")]
    [InlineData("connection")]
    [InlineData("queue-wait")]
    [InlineData("bearer")]
    public void CaptureDefaultOptions_ConfigurationDrift_RefusesMeasurement(string setting)
    {
        var options = new ConsumerVfsOptions { Enabled = true };
        switch (setting)
        {
            case "content":
                options.MaximumConcurrentRequests++;
                break;
            case "connection":
                options.MaximumConcurrentRequestsPerConnection++;
                break;
            case "queue-wait":
                options.MaximumQueueWaitMilliseconds++;
                break;
            case "bearer":
                options.AllowDevelopmentBearer = true;
                break;
        }

        Should.Throw<InvalidOperationException>(() => ChdCdInteractiveReadHost.CaptureDefaultOptions(options));
    }

    [Fact]
    public void CaptureDefaultOptions_ObservedDefaults_ReturnsAnImmutableCompleteSnapshot()
    {
        var options = new ConsumerVfsOptions { Enabled = true };
        var captured = ChdCdInteractiveReadHost.CaptureDefaultOptions(options);
        options.MaximumConcurrentRequests++;

        captured.GetProperty(nameof(options.MaximumConcurrentRequests)).GetInt32().ShouldBe(new ConsumerVfsOptions().MaximumConcurrentRequests);
        captured.GetProperty(nameof(options.AllowDevelopmentBearer)).GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_StopOrAppDisposalFails_StillDisposesDecoderAndCertificateOnce(bool failStop)
    {
        var app = Substitute.For<IHost, IAsyncDisposable>();
        var injected = new InvalidOperationException("injected host shutdown failure");
        app.StopAsync(CancellationToken.None).Returns(failStop ? Task.FromException(injected) : Task.CompletedTask);
        ((IAsyncDisposable)app).DisposeAsync().Returns(_ => failStop ? ValueTask.CompletedTask : ValueTask.FromException(injected));
        var runtime = ConvergedReadDecoder.Managed();
        var certificate = ChdCdInteractiveReadHost.CreateCertificate();
        var host = new ChdCdInteractiveReadHost(app, certificate, runtime, new(runtime.Decoder),
            ChdCdInteractiveReadHost.CaptureDefaultOptions(new ConsumerVfsOptions { Enabled = true }));

        (await Should.ThrowAsync<InvalidOperationException>(async () => await host.DisposeAsync())).ShouldBeSameAs(injected);
        await host.DisposeAsync();

        await app.Received(1).StopAsync(CancellationToken.None);
        await ((IAsyncDisposable)app).Received(1).DisposeAsync();
        Should.Throw<CryptographicException>(() => certificate.GetCertHash());
        var raw = Substitute.For<IRandomAccessContent>();
        await Should.ThrowAsync<ObjectDisposedException>(() => runtime.Decoder.DecodeAsync(
            RomRepresentationEncoding.Identity, raw, CancellationToken.None, artifact: null, admissionTimeout: null));
    }
}
