using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>The real host compositions refuse to start on partial, blank or broken native CHD configuration.</summary>
/// <remarks>Every case fails before libchdr loads, so each uses its own build copy; only the CHD startup gate runs, so no
/// case reaches PostgreSQL.</remarks>
public sealed class ChdHostStartupTests : IDisposable
{
    private const string TestJwtSecret = "chd-host-startup-test-secret-at-least-32-characters";
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=romd;Username=romd;Timeout=1";
    private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-host-startup-").FullName;

    [Theory]
    [InlineData("admin", "chdman-only")]
    [InlineData("consumer", "chdman-only")]
    [InlineData("worker", "chdman-only")]
    [InlineData("worker", "libchdr-only")]
    [InlineData("admin", "blank-libchdr")]
    [InlineData("consumer", "blank-libchdr")]
    [InlineData("worker", "blank-libchdr")]
    public async Task Startup_PartialOrBlankNativeConfiguration_RefusesBeforeReadingABuild(string host, string configuration)
    {
        var native = configuration switch
        {
            "chdman-only" => Native(chdman: Chdman()),
            "libchdr-only" => Native(libchdr: Libchdr()),
            _ => Native(libchdr: "", chdman: host == "worker" ? Chdman() : null)
        };

        var error = (await StartupErrorAsync(host, native)).ShouldBeOfType<InvalidOperationException>();

        error.Message.ShouldBe(configuration switch
        {
            "chdman-only" => "Romd:Native:ChdmanDirectory requires Romd:Native:LibchdrDirectory.",
            "libchdr-only" => "Worker CHD encoding requires Romd:Native:ChdmanDirectory.",
            _ => "Romd:Native:LibchdrDirectory must be an absolute directory path."
        });
    }

    [Theory]
    [InlineData("admin", "ManifestMissing")]
    [InlineData("consumer", "ManifestMissing")]
    [InlineData("worker", "ManifestMissing")]
    [InlineData("admin", "BinaryMissing")]
    [InlineData("consumer", "BinaryMissing")]
    [InlineData("worker", "BinaryMissing")]
    public async Task Startup_LibchdrDirectoryWithoutRecordOrBinary_FailsClosed(string host, string cause)
    {
        string libchdr = cause == "ManifestMissing"
            ? Directory.CreateDirectory(Path.Combine(_root, "libchdr-without-record")).FullName
            : Libchdr();
        if (cause == "BinaryMissing")
            File.Delete(ChdNativeCandidate.FromBuildRecord(libchdr).BinaryPath);

        var failure = await StartupErrorAsync(host, Native(libchdr, host == "worker" ? Chdman() : null));
        var error = failure.ShouldBeOfType<ChdNativeCandidateException>(failure?.ToString());

        error.Rejection.Cause.ShouldBe(cause);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<Exception?> StartupErrorAsync(string host, IReadOnlyDictionary<string, string?> native)
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(dataDirectory, "wwwroot"));
        File.WriteAllText(Path.Combine(dataDirectory, "wwwroot", "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);
        return host switch
        {
            "admin" => Record.Exception(() => StartWebHost<Romd.Admin.Host.Program>(dataDirectory, native, management: true)),
            "consumer" => Record.Exception(() => StartWebHost<Romd.Consumer.Host.Program>(dataDirectory, native, management: false)),
            _ => await Record.ExceptionAsync(async () =>
            {
                using var worker = Romd.Worker.Host.Program.CreateHostBuilder([])
                    .UseEnvironment("Development")
                    .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>(native)
                        {
                            ["Romd:DataDirectory"] = dataDirectory,
                            ["Romd:JwtSecret"] = TestJwtSecret,
                            ["Romd:JwtIssuer"] = "RomdChdHostStartupTests",
                            ["Romd:JwtAudience"] = "RomdChdHostStartupTests",
                            ["ConnectionStrings:Hangfire"] = UnreachableConnectionString,
                            [$"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}"] = UnreachableConnectionString
                        }))
                    .ConfigureServices(KeepOnlyNativeStartupCheck)
                    .Build();
                await worker.StartAsync();
                await worker.StopAsync();
            })
        };
    }

    private static void StartWebHost<TProgram>(string dataDirectory, IReadOnlyDictionary<string, string?> native,
        bool management) where TProgram : class
    {
        using var factory = new NativeStartupFactory<TProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseWebRoot(Path.Combine(dataDirectory, "wwwroot"));
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            builder.UseSetting("Romd:DataDirectory", dataDirectory);
            builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
            builder.UseSetting($"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}", UnreachableConnectionString);
            foreach (var (key, value) in native)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                KeepOnlyNativeStartupCheck(services);
                if (management)
                    services.AddHangfire((_, configuration) => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseInMemoryStorage());
            });
        });
        factory.CreateClient().Dispose();
    }

    /// <summary>Keep the native failure visible when the real entry point immediately disposes its failed host.</summary>
    private sealed class NativeStartupFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
    {
        private readonly TaskCompletionSource _observeStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(ChdDeploymentRuntime));
            var createRuntime = registration.ImplementationFactory.ShouldNotBeNull();
            services.Replace(ServiceDescriptor.Singleton<ChdDeploymentRuntime>(provider =>
            {
                _observeStartup.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return (ChdDeploymentRuntime)createRuntime(provider);
            }));
        });

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = builder.Build();
            try
            {
                // ASP.NET Core 10.0.13 DeferredHost.StartAsync reads ApplicationStarted before awaiting entry-point faults.
                // Install that waiter before native resolution can fail and dispose the application's provider.
                var startup = host.StartAsync();
                _observeStartup.TrySetResult();
                startup.GetAwaiter().GetResult();
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
            finally { _observeStartup.TrySetResult(); }
        }
    }

    private static void KeepOnlyNativeStartupCheck(IServiceCollection services)
    {
        foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType != typeof(ChdAssetsStartupCheck)).ToArray())
            services.Remove(service);
    }

    private static Dictionary<string, string?> Native(string? libchdr = null, string? chdman = null)
    {
        var native = new Dictionary<string, string?>();
        if (libchdr is not null)
            native["Romd:Native:LibchdrDirectory"] = libchdr;
        if (chdman is not null)
            native["Romd:Native:ChdmanDirectory"] = chdman;
        return native;
    }

    private string Libchdr() => NativeTestBuilds.CopyBuild(NativeTestBuilds.LibchdrDirectory,
        Path.Combine(_root, "libchdr-" + Guid.NewGuid().ToString("N")));

    private string Chdman() => NativeTestBuilds.CopyBuild(NativeTestBuilds.ChdmanDirectory,
        Path.Combine(_root, "chdman-" + Guid.NewGuid().ToString("N")));
}
