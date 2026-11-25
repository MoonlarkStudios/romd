using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using Romd.Infrastructure.Providers.Igdb;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbProviderOptionsRegistrationTests
{
    [Fact]
    public void AddInfrastructure_NoProviderConfiguration_DoesNotBindIgdbCredentials()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IgdbProviderOptions>>().Value;

        options.ClientId.ShouldBeNull();
        options.ClientSecret.ShouldBeNull();
    }

    [Fact]
    public void AddInfrastructure_ProviderConfiguration_BindsIgdbCredentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{IgdbProviderOptions.SectionName}:ClientId"] = "provider-client-id",
                [$"{IgdbProviderOptions.SectionName}:ClientSecret"] = "provider-client-secret"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IgdbProviderOptions>>().Value;

        options.ClientId.ShouldBe("provider-client-id");
        options.ClientSecret.ShouldBe("provider-client-secret");
    }

    [Fact]
    public void AddInfrastructure_RomdConfiguration_DoesNotBindIgdbCredentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Romd:IgdbClientId"] = "romd-client-id",
                ["Romd:IgdbClientSecret"] = "romd-client-secret"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IgdbProviderOptions>>().Value;

        options.ClientId.ShouldBeNull();
        options.ClientSecret.ShouldBeNull();
    }
}
