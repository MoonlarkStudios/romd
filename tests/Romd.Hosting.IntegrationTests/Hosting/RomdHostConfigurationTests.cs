using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Host.Hubs;
using Romd.Hosting;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class RomdHostConfigurationTests
{
    [Fact]
    public void AddRomdAdminAuth_ConfiguresAdminAudience()
    {
        var services = new ServiceCollection();
        var romdOptions = CreateRomdOptions();
        var configuration = CreateConfiguration([
            new("Romd:AdminHost:JwtAudience", "romd-admin")
        ]);

        services.AddRomdAdminAuth(romdOptions, configuration, new TestHostEnvironment());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<RomdSurfaceAuthOptions>().Audience.ShouldBe("romd-admin");
    }

    [Fact]
    public void AddRomdConsumerAuth_ConfiguresConsumerAudience()
    {
        var services = new ServiceCollection();
        var romdOptions = CreateRomdOptions();
        var configuration = CreateConfiguration([
            new("Romd:ConsumerHost:JwtAudience", "romd-consumer")
        ]);

        services.AddRomdConsumerAuth(romdOptions, configuration, new TestHostEnvironment());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<RomdSurfaceAuthOptions>().Audience.ShouldBe("romd-consumer");
    }

    [Fact]
    public void AddRomdHostHttp_RegistersSeparateConfiguredCorsPolicies()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration([
            new("Romd:AdminHost:CorsOrigins:0", "https://admin.romd.test"),
            new("Romd:ConsumerHost:CorsOrigins:0", "https://consumer.romd.test"),
            new("Romd:ConsumerHost:AllowCorsCredentials", "false")
        ]);
        var environment = new TestHostEnvironment();

        services
            .AddRomdAdminHttp(configuration, environment)
            .AddRomdConsumerHttp(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var adminPolicy = corsOptions.GetPolicy(RomdCorsPolicyNames.Admin);
        var consumerPolicy = corsOptions.GetPolicy(RomdCorsPolicyNames.Consumer);

        adminPolicy.ShouldNotBeNull();
        adminPolicy.Origins.ShouldBe(["https://admin.romd.test"]);
        adminPolicy.SupportsCredentials.ShouldBeTrue();

        consumerPolicy.ShouldNotBeNull();
        consumerPolicy.Origins.ShouldBe(["https://consumer.romd.test"]);
        consumerPolicy.SupportsCredentials.ShouldBeFalse();
    }

    [Fact]
    public void AddRomdConsumerHttp_ProductionCorsRequiresConfiguredConsumerOrigins()
    {
        var services = new ServiceCollection();

        services.AddRomdConsumerHttp(CreateConfiguration([]), new TestHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>().Value;
        var consumerPolicy = corsOptions.GetPolicy(RomdCorsPolicyNames.Consumer);

        consumerPolicy.ShouldNotBeNull();
        consumerPolicy.Origins.ShouldBeEmpty();
        consumerPolicy.SupportsCredentials.ShouldBeTrue();
    }

    [Fact]
    public void AddRomdConsumerHttp_DoesNotRegisterRealtimeHubsOrTransports()
    {
        var services = new ServiceCollection();

        services.AddRomdConsumerHttp(CreateConfiguration([]), new TestHostEnvironment());

        services.Any(descriptor => descriptor.ServiceType == typeof(IHubContext<JobHub>)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(IHubContext<SystemHub>)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType.FullName?.Contains("HubLifetimeManager", StringComparison.Ordinal) == true)
            .ShouldBeFalse();
    }

    private static IConfiguration CreateConfiguration(KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static IRomdOptions CreateRomdOptions()
    {
        // The OpenIddict server is registered during AddRomdAuth and reads the signing key, so the
        // key must exist deterministically for these registration-only tests.
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(dataDirectory);
        options.JwtSecret.Returns("0123456789abcdef0123456789abcdef");

        return options;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Romd.Host.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
