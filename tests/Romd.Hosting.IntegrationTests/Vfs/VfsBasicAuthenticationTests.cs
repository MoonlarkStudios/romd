using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Consumer.Application.Vfs;
using Romd.Hosting.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsBasicAuthenticationTests
{
    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("https", false)]
    [InlineData("https", true)]
    public async Task AuthenticateRequest_TransportGuardNeverReplacesCredentialVerification(string scheme, bool validCredentials)
    {
        var connections = Substitute.For<IVfsConnections>();
        var connectionId = Guid.NewGuid();
        string username = $"romd-{connectionId:N}";
        connections.AuthenticateAsync(username, "test-credential", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AuthenticatedVfsConnection?>(validCredentials ? new(Guid.NewGuid(), connectionId) : null));
        await using var services = new ServiceCollection()
            .AddSingleton(new ConsumerVfsOptions { Enabled = true })
            .AddSingleton(connections)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/vfs/default/file";
        context.Request.Scheme = scheme;
        // A raw forwarded header cannot change Request.IsHttps; trusted proxy processing is separate.
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":test-credential"));

        var result = await VfsBasicAuthenticationHandler.AuthenticateRequestAsync(context);

        result.Succeeded.ShouldBe(scheme == "https" && validCredentials);
        if (scheme == "https")
            await connections.Received(1).AuthenticateAsync(username, "test-credential", context.RequestAborted);
        else
        {
            connections.ReceivedCalls().ShouldBeEmpty();
            result.Failure.ShouldNotBeNull().Message.ShouldBe("VFS credentials require HTTPS.");
        }
    }
}
