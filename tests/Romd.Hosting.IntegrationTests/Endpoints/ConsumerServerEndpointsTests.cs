using System.Net;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerServerEndpointsTests : IDisposable
{
    private const string TestJwtSecret = "consumer-server-endpoint-test-secret-at-least-32-characters";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-server-endpoints-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetIdentity_Anonymous_ReturnsExactCanonicalPublicIdentity()
    {
        string dataDirectory = CreateDataDirectory();
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>(dataDirectory, isManagementHost: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/server/identity");
        string content = await response.Content.ReadAsStringAsync();
        string expectedId = factory.Services
            .GetRequiredService<IServerInstanceIdentity>()
            .InstanceId
            .ToString("D");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.ShouldBe($"{{\"instanceId\":\"{expectedId}\"}}");
        response.Headers.WwwAuthenticate.ShouldBeEmpty();

        var endpoint = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/api/server/identity");
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldBeEmpty();
    }

    [Fact]
    public void GetIdentity_CorruptExistingIdentity_FailsHostStartupWithoutReplacement()
    {
        string dataDirectory = CreateDataDirectory();
        string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        File.WriteAllText(identityPath, "corrupt");
        using var factory = CreateFactory<Romd.Consumer.Host.Program>(dataDirectory, isManagementHost: false);

        Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        File.ReadAllText(identityPath).ShouldBe("corrupt");
    }

    [Fact]
    public void AdminHost_CorruptExistingIdentity_FailsHostStartupWithoutReplacement()
    {
        string dataDirectory = CreateDataDirectory();
        string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        File.WriteAllText(identityPath, "corrupt");
        using var factory = CreateFactory<Romd.Admin.Host.Program>(dataDirectory, isManagementHost: true);

        Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        File.ReadAllText(identityPath).ShouldBe("corrupt");
    }

    [Fact]
    public async Task ConsumerAndAdminHosts_SharedDataDirectory_LoadSameIdentity()
    {
        string dataDirectory = CreateDataDirectory();
        await using var consumerFactory = CreateFactory<Romd.Consumer.Host.Program>(dataDirectory, isManagementHost: false);
        await using var adminFactory = CreateFactory<Romd.Admin.Host.Program>(dataDirectory, isManagementHost: true);
        using var consumerClient = consumerFactory.CreateClient();
        using var adminClient = adminFactory.CreateClient();

        Guid consumerId = consumerFactory.Services.GetRequiredService<IServerInstanceIdentity>().InstanceId;
        Guid adminId = adminFactory.Services.GetRequiredService<IServerInstanceIdentity>().InstanceId;
        var response = await consumerClient.GetAsync("/api/server/identity");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        adminId.ShouldBe(consumerId);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe($"{{\"instanceId\":\"{consumerId:D}\"}}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateDataDirectory()
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WebApplicationFactory<TProgram> CreateFactory<TProgram>(
        string dataDirectory,
        bool isManagementHost)
        where TProgram : class
    {
        string webRoot = Path.Combine(dataDirectory, $"wwwroot-{typeof(TProgram).Name}");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        return new WebApplicationFactory<TProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<ServerInstanceIdentityInitializer>();

                    if (isManagementHost)
                    {
                        services.AddHangfire((_, configuration) => configuration
                            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                            .UseSimpleAssemblyNameTypeSerializer()
                            .UseRecommendedSerializerSettings()
                            .UseInMemoryStorage());
                    }
                });
            });
    }
}
