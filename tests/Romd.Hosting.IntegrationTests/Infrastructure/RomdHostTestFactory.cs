using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Identity;
using Romd.Persistence;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Boots a real admin or consumer host without a database: hosted services are removed, so
///     only composition (routes, authorization, OpenAPI, JSON and error handling) is exercised.
///     Tests that reach a handler needing persistence replace it in <paramref name="configureServices" />.
/// </summary>
internal static class RomdHostTestFactory
{
    /// <summary>
    ///     Lets services that depend on the database be constructed without one, and keeps a developer
    ///     environment's <c>ConnectionStrings__Romd</c> from satisfying a test that CI runs without it.
    ///     A test that needs PostgreSQL supplies its connection string in <c>settings</c>.
    /// </summary>
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";

    public static WebApplicationFactory<TProgram> Create<TProgram>(
        string dataDirectory,
        string environment,
        string jwtSecret,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string>? settings = null)
        where TProgram : class
    {
        string webRoot = Path.Combine(dataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        return new WebApplicationFactory<TProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting("Romd:JwtSecret", jwtSecret);
                builder.UseSetting($"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}", UnreachableDatabase);
                foreach ((string key, string value) in settings ?? new Dictionary<string, string>())
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    configureServices?.Invoke(services);
                });
            });
    }

    /// <summary>
    ///     The admin host under the Testing environment, which skips the Hangfire PostgreSQL client,
    ///     so job enqueueing uses in-memory storage.
    /// </summary>
    public static WebApplicationFactory<Romd.Admin.Host.Program> CreateAdmin(
        string dataDirectory,
        string jwtSecret,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string>? settings = null) =>
        Create<Romd.Admin.Host.Program>(
            dataDirectory,
            "Testing",
            jwtSecret,
            services =>
            {
                services.AddHangfire((_, config) => config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseInMemoryStorage());
                configureServices?.Invoke(services);
            },
            settings);
}
