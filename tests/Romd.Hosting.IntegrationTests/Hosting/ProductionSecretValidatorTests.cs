using System;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Romd.Host.Configuration;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ProductionSecretValidatorTests
{
    [Fact]
    public void Validate_ProductionWithDefaultSecrets_Throws()
    {
        // The resolved environment is Production (which ASP.NET also defaults to when the env var is unset);
        // validation must fire off the IHostEnvironment, not a raw env-var read.
        var options = new RomdOptions();

        Should.Throw<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(Environment("Production"), options));
    }

    [Fact]
    public void Validate_ProductionWithStrongSecrets_DoesNotThrow()
    {
        var options = new RomdOptions
        {
            JwtSecret = new string('k', 48),
            DefaultAdminPassword = "S0me-Strong-Passw0rd!"
        };

        Should.NotThrow(() => ProductionSecretValidator.Validate(Environment("Production"), options));
    }

    [Fact]
    public void Validate_NonProductionWithDefaultSecrets_DoesNotThrow()
    {
        var options = new RomdOptions();

        Should.NotThrow(() => ProductionSecretValidator.Validate(Environment("Development"), options));
    }

    private static IHostEnvironment Environment(string name) => new FakeHostEnvironment(name);

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
