using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class NativeWebDavAuthenticationProbeTests
{
    [Theory]
    [InlineData("/vfs/library/file", "127.0.0.1", true, true)]
    [InlineData("/vfs/library/file", "::1", true, true)]
    [InlineData("/vfs/library/file", "192.0.2.1", true, false)]
    [InlineData("/vfs/library/file", "127.0.0.1", false, false)]
    public async Task Adapter_OnlyAcceptsLoopbackFixtureCredentials(string path, string address, bool correct, bool allowed)
    {
        var adapter = new NativeWebDavAuthenticationProbe { BearerToken = "issued-fixture-token" };
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("davprobe:" + (correct ? adapter.Password : "wrong")));
        bool called = false;
        await adapter.InvokeAsync(context, next =>
        {
            called = true;
            next.Request.Headers.Authorization.ToString().ShouldBe("Bearer issued-fixture-token");
            return Task.CompletedTask;
        });
        called.ShouldBe(allowed);
        if (!allowed)
            context.Response.StatusCode.ShouldBe(401);
    }

    [Theory]
    [InlineData("/connect/token", "Basic fixture")]
    [InlineData("/vfs-other", "Basic fixture")]
    [InlineData("/vfs/library/file", "Bearer original-token")]
    public async Task Adapter_PreservesRequestsOutsideCredentialTranslation(string path, string authorization)
    {
        var adapter = new NativeWebDavAuthenticationProbe { BearerToken = "replacement" };
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = authorization;
        bool called = false;
        await adapter.InvokeAsync(context, next =>
        {
            called = true;
            next.Request.Headers.Authorization.ToString().ShouldBe(authorization);
            return Task.CompletedTask;
        });
        called.ShouldBeTrue();
    }
}
