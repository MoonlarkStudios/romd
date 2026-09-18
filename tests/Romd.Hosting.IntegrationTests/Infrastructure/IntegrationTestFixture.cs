using Romd.Persistence.ReferenceData;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Identity;
using Romd.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

public class IntegrationTestFixture : WebApplicationFactory<Romd.Admin.Host.Program>, IAsyncLifetime
{
    // Test JWT secret - must be at least 32 characters
    private const string TestJwtSecret = "integration-test-secret-key-at-least-32-characters-long";
    private readonly string _tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _originalEnvironmentVariables = new(StringComparer.Ordinal)
    {
        ["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
        ["ASPNETCORE_CONTENTROOT"] = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT"),
        ["DOTNET_ENVIRONMENT"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        ["DOTNET_USE_POLLING_FILE_WATCHER"] = Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER"),
        ["hostBuilder__reloadConfigOnChange"] = Environment.GetEnvironmentVariable("hostBuilder__reloadConfigOnChange"),
        ["Logging__LogLevel__Default"] = Environment.GetEnvironmentVariable("Logging__LogLevel__Default"),
        ["Logging__LogLevel__Hangfire"] = Environment.GetEnvironmentVariable("Logging__LogLevel__Hangfire"),
        ["Logging__LogLevel__Microsoft.AspNetCore"] = Environment.GetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore"),
        ["Logging__LogLevel__Microsoft.EntityFrameworkCore"] = Environment.GetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore"),
        ["Serilog__MinimumLevel__Default"] = Environment.GetEnvironmentVariable("Serilog__MinimumLevel__Default"),
        ["Romd__DataDirectory"] = Environment.GetEnvironmentVariable("Romd__DataDirectory"),
        ["Romd__JwtSecret"] = Environment.GetEnvironmentVariable("Romd__JwtSecret"),
        ["Romd__JwtIssuer"] = Environment.GetEnvironmentVariable("Romd__JwtIssuer"),
        ["Romd__JwtAudience"] = Environment.GetEnvironmentVariable("Romd__JwtAudience")
    };
    private PostgreSqlTestDatabase? _database;

    // Cached id of the seeded admin user, used to authenticate via the test scheme.
    private Guid? _adminUserId;

    /// <summary>Shared data directory (Romd:DataDirectory) for this test run.</summary>
    public string DataDirectoryPath => _tempDataDirectory;

    /// <summary>Allowlisted root (Romd:AllowedImportPaths) for path-import tests.</summary>
    public string ImportRootPath => Path.Combine(_tempDataDirectory, "import-root");

    // Every fixture gets its own PostgreSQL database copy and data directory for full isolation.

    public async Task InitializeAsync()
    {
        ApplyTestEnvironment();

        _database = PostgreSqlTestDatabase.Create();
        await _database.ProvisionAsync();

        // Start the application with test-specific storage and workers.
        _ = Services;

        await InitializeDatabaseAsync();

        // Resolve the seeded admin id after the app is ready.
        _adminUserId = await ResolveAdminUserIdAsync();
    }

    public new async Task DisposeAsync()
    {
        Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        RestoreEnvironment();
    }

    /// <summary>
    ///     Creates an HttpClient authenticated as the seeded admin via the test scheme.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        if (_adminUserId is { } adminUserId)
        {
            client.WithTestUser(adminUserId, "admin@localhost", [RomdRoleType.Admin]);
        }

        return client;
    }

    private async Task<Guid?> ResolveAdminUserIdAsync()
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();

        // The AdminSeeder creates admin@localhost by default.
        var adminUser = await userManager.FindByEmailAsync("admin@localhost");
        return adminUser?.Id;
    }

    private async Task InitializeDatabaseAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        await RunSeederAsync<AdminSeeder>(cancellationToken);
        await RunSeederAsync<SharedReferenceDataSeeder>(cancellationToken);
        await RunSeederAsync<RomdOpenIddictApplicationSeeder>(cancellationToken);

        await WaitForPlatformsAsync(cancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseWebRoot(Path.Combine(_tempDataDirectory, "wwwroot"));
        builder.UseSetting("Romd:DataDirectory", _tempDataDirectory);
        builder.UseSetting(
            $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
            _database!.ConnectionString);
        builder.UseSetting("Romd:AllowedImportPaths:0", ImportRootPath);
        Directory.CreateDirectory(ImportRootPath);

        // JWT settings - single source of truth via RomdOptions
        builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
        builder.UseSetting("Romd:JwtIssuer", "RomdTests");
        builder.UseSetting("Romd:JwtAudience", "RomdTests");

        // Ensure the directory exists before the app tries to use it
        Directory.CreateDirectory(_tempDataDirectory);
        Directory.CreateDirectory(Path.Combine(_tempDataDirectory, "wwwroot"));

        // The OpenIddict server now runs on the admin host too and reads the signing key at
        // registration; the worker normally creates it, so create it here for the in-process host.
        OpenIddictSigningKey.EnsureCreated(_tempDataDirectory);

        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<DbContextOptions<RomdDbContext>>();

            services.AddDbContext<RomdDbContext>((sp, options) =>
            {
                PostgreSqlConfiguration.Configure(options, _database!.ConnectionString);
                options.UseOpenIddict();
                options.EnableDetailedErrors();
                options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>(), new SearchDocumentInterceptor());
            });

            services
                .AddRomdWorkerCurationServices()
                .AddRomdHangfireJobPipeline()
                .AddRomdJobExecution();

            services.AddHangfire((serviceProvider, config) => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseFilter(serviceProvider.GetRequiredService<HangfireJobStateSyncFilter>())
                .UseInMemoryStorage());

            // This fixture already owns in-process worker execution; dispatch now consumes the
            // same transactional intents as the production worker instead of request-time enqueue.
            services.AddHostedService<Romd.Infrastructure.Jobs.JobDispatchWorker>();
            services.AddHangfireServer(options =>
            {
                options.Queues = ["default", "upload", "enrichment"];
                options.WorkerCount = 1;
            });

            services.AddTransient<AdminSeeder>();
            services.AddTransient<SharedReferenceDataSeeder>();
            services.AddTransient<RomdOpenIddictApplicationSeeder>();

            services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                // Aggressive cleanup to prevent disk bloat from repeated test runs
                if (Directory.Exists(_tempDataDirectory))
                {
                    Directory.Delete(_tempDataDirectory, true);
                }
            }
            catch
            {
                // Ignored - best effort cleanup
            }
        }
    }

    private async Task RunSeederAsync<TSeeder>(CancellationToken cancellationToken)
        where TSeeder : IHostedService
    {
        await using var scope = Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<TSeeder>();
        await seeder.StartAsync(cancellationToken);
    }

    private void ApplyTestEnvironment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        Environment.SetEnvironmentVariable("hostBuilder__reloadConfigOnChange", "false");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Hangfire", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning");
        Environment.SetEnvironmentVariable("Serilog__MinimumLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Romd__DataDirectory", _tempDataDirectory);
        Environment.SetEnvironmentVariable("Romd__JwtSecret", TestJwtSecret);
        Environment.SetEnvironmentVariable("Romd__JwtIssuer", "RomdTests");
        Environment.SetEnvironmentVariable("Romd__JwtAudience", "RomdTests");
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _originalEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private async Task WaitForPlatformsAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var platformRepo = scope.ServiceProvider.GetRequiredService<IPlatformRepository>();
        if (!(await platformRepo.GetAllAsync(cancellationToken)).Any())
        {
            throw new TimeoutException("Application did not seed platforms in time.");
        }
    }
}

[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration Tests";
}
