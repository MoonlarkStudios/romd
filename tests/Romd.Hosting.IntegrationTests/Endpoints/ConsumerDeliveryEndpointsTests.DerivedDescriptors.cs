using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerDeliveryEndpointsTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("bad-signature")]
    [InlineData("expired")]
    [InlineData("wrong-purpose")]
    [InlineData("stale")]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("wrong-hash")]
    public async Task DerivedDescriptor_ActualHttp_ValidatesCapabilityAndSourceBeforeOpeningAndVerifiesBytesBeforeHeaders(string scenario)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        byte[] bytes = "FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\n"u8.ToArray();
        var identity = MediaFactFixtures.Identity(bytes);
        var captured = new DerivedDescriptorBinding(7, Guid.NewGuid(), 11, 13, "folder/disc.cue", 17, 1, 19, identity, "fixture-generator/v1");
        var retained = Substitute.For<IConsumerDerivedDescriptorSource>();
        var artifacts = Substitute.For<IConsumerContentArtifactResolver>();
        var calls = new List<string>();
        retained.IsRetainedAsync(captured, Arg.Any<CancellationToken>()).Returns(_ => { calls.Add("retained"); return scenario != "stale"; });
        byte[] actual = scenario switch { "short" => bytes[..^1], "long" => [.. bytes, 0], "wrong-hash" => bytes.Select(b => (byte)(b ^ 1)).ToArray(), _ => bytes };
        var stream = new DescriptorTrackingStream(actual);
        artifacts.ResolveAsync(identity.Sha256, Arg.Any<CancellationToken>()).Returns(_ =>
        { calls.Add("cas"); return Task.FromResult<Stream?>(scenario == "missing" ? null : stream); });
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConsumerDerivedDescriptorSource>();
            services.AddSingleton(retained);
            services.RemoveAll<IConsumerContentArtifactResolver>();
            services.AddSingleton(artifacts);
        }));
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IConsumerDerivedDescriptorGrantIssuer>();
        var grant = new ConsumerDerivedDescriptorGrant(Guid.NewGuid(), 2, 3, captured);
        var issued = issuer.Issue(grant);
        string url = issued.DownloadUrl;
        if (scenario == "bad-signature")
            url = url[..(url.LastIndexOf('/') + 1)] + ReplaceFirstSignatureCharacter(url[(url.LastIndexOf('/') + 1)..]);
        if (scenario == "expired")
            fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));
        if (scenario == "wrong-purpose")
        {
            var bios = scope.ServiceProvider.GetRequiredService<IConsumerBiosGrantIssuer>().IssueBiosDownloadGrant(new(grant.UserId, 2, 3, 19, identity.Sha256, identity.Size));
            url = "/delivery/derived-descriptor/" + bios.DownloadUrl[(bios.DownloadUrl.LastIndexOf('/') + 1)..];
        }
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        using var response = await client.SendAsync(request);
        bool invalid = scenario is "bad-signature" or "expired" or "wrong-purpose";
        response.StatusCode.ShouldBe(scenario == "valid" ? HttpStatusCode.OK : invalid ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
        calls.ShouldBe(invalid ? [] : scenario == "stale" ? ["retained"] : ["retained", "cas"]);
        if (scenario == "valid")
        {
            (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
            response.Content.Headers.ContentLength.ShouldBe(identity.Size);
            response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("disc.cue");
            response.Headers.CacheControl!.Private.ShouldBeTrue();
            response.Headers.CacheControl.NoStore.ShouldBeTrue();
            response.Headers.Contains("Accept-Ranges").ShouldBeFalse();
            response.Content.Headers.Contains("Content-Range").ShouldBeFalse();
        }
        else
            response.Content.Headers.ContentDisposition.ShouldBeNull();
        stream.WasDisposed.ShouldBe(!invalid && scenario is not "stale" and not "missing");
        await artifacts.DidNotReceiveWithAnyArgs().ResolveAsync(default(RomDeliveryTarget)!, default);
    }
    private sealed class DescriptorTrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { WasDisposed = true; return base.DisposeAsync(); }
    }
}
