using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Romd.Host.Configuration;
using Romd.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ProductionSecretValidatorTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Theory]
    [InlineData("")]
    [InlineData("short-secret")]
    [InlineData("                                        ")]
    public void AddRomdOptions_InvalidTokenSecret_RejectsBeforeCreatingDataDirectory(string secret)
    {
        string dataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"romd-invalid-options-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:DataDirectory"] = dataDirectory,
            ["Romd:JwtSecret"] = secret,
            ["Romd:DefaultAdminPassword"] = "S0me-Strong-Passw0rd!"
        }).Build();

        try
        {
            var exception = Should.Throw<ValidationException>(() => new ServiceCollection().AddRomdOptions(
                configuration, Environment("Production"), ProductionSecretValidator.Validate));

            exception.Message.ShouldContain(nameof(RomdOptions.JwtSecret));
            System.IO.Directory.Exists(dataDirectory).ShouldBeFalse();
        }
        finally
        {
            if (System.IO.Directory.Exists(dataDirectory))
                System.IO.Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ProductionWithDefaultSecrets_Throws()
    {
        // The resolved environment is Production (which ASP.NET also defaults to when the env var is unset);
        // validation must fire off the IHostEnvironment, not a raw env-var read.
        var options = new RomdOptions();

        Should.Throw<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(Environment("Production"), options, EmptyConfiguration));
    }

    [Fact]
    public void Validate_ProductionWithStrongSecrets_DoesNotThrow()
    {
        var options = StrongOptions();

        Should.NotThrow(() => ProductionSecretValidator.Validate(Environment("Production"), options, EmptyConfiguration));
    }

    [Fact]
    public void Validate_ProductionWithEnvExampleJwtSecret_Throws()
    {
        var options = StrongOptions();
        options.JwtSecret = "local-only-replace-this-jwt-secret-32-plus-chars";

        var exception = Should.Throw<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(Environment("Production"), options, EmptyConfiguration));

        exception.Message.ShouldContain("Configure Romd:JwtSecret with a secure value.");
    }

    [Fact]
    public void Validate_ProductionWithEnvExampleAdminPassword_Throws()
    {
        var options = StrongOptions();
        options.DefaultAdminPassword = "LocalOnly123";

        var exception = Should.Throw<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(Environment("Production"), options, EmptyConfiguration));

        exception.Message.ShouldContain("Configure Romd:DefaultAdminPassword with a secure value.");
    }

    [Fact]
    public void Validate_ProductionWithEnvExampleDeliverySigningSecret_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Romd:ConsumerDelivery:SigningSecret"] = "local-only-replace-this-delivery-secret-32-bytes-min"
            })
            .Build();

        var exception = Should.Throw<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(Environment("Production"), StrongOptions(), configuration));

        exception.Message.ShouldContain("Configure Romd:ConsumerDelivery:SigningSecret with a secure value.");
    }

    [Fact]
    public void Validate_NonProductionWithDefaultSecrets_DoesNotThrow()
    {
        var options = new RomdOptions();

        Should.NotThrow(() => ProductionSecretValidator.Validate(Environment("Development"), options, EmptyConfiguration));
    }

    private static RomdOptions StrongOptions() => new()
    {
        JwtSecret = new string('k', 48),
        DefaultAdminPassword = "S0me-Strong-Passw0rd!"
    };

    private static IHostEnvironment Environment(string name) => new FakeHostEnvironment(name);

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
