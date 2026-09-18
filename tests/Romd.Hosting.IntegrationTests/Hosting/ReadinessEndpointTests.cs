using System.Net;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Readiness;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Truthful readiness semantics of GET /health/ready: 200 with status "ready" when every check
///     passes, 503 with status "unready" when the critical database check fails (unreachable
///     server, or a schema behind the model with pending migrations), 200 with status "degraded" when
///     only a non-critical check fails — while GET /health stays a plain 200 liveness signal
///     throughout.
/// </summary>
public sealed class ReadinessEndpointTests
{
    private const string TestJwtSecret = "readiness-endpoint-test-secret-at-least-32-characters";
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=romd;Username=romd;Timeout=1";

    [Fact]
    public async Task AdminHost_HealthyDependencies_ReportsReadyAndLivenessStaysOk()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));

            await using var factory = CreateFactory<Romd.Admin.Host.Program>(
                dataDirectory,
                database.ConnectionString);
            AnnounceWorkerHeartbeat(factory.Services);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");
            using var livenessResponse = await client.GetAsync("/health");

            readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            livenessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("status").GetString().ShouldBe("ready");
            GetCheckStatuses(body).Keys.ShouldBe(
                ["database", "application-schema", "disk", "storage", "hangfire-storage", "outbox", "worker"],
                ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AdminHost_UnreachableDatabase_ReportsUnreadyWhileLivenessStaysOk()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            // Nothing listens on the configured port, so the critical database check fails while
            // the process itself is alive.
            await using var factory = CreateFactory<Romd.Admin.Host.Program>(
                dataDirectory,
                UnreachableConnectionString);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");
            using var livenessResponse = await client.GetAsync("/health");

            readyResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            livenessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("status").GetString().ShouldBe("unready");
            GetCheckStatuses(body)["database"].ShouldBe("unready");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AdminHost_PendingMigrations_ReportsUnreadyWhileLivenessStaysOk()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            // The schema exists and is reachable, but the migration history says nothing was
            // applied, so the database check sees the baseline as pending.
            await MakeMigrationsPendingAsync(database);
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));

            await using var factory = CreateFactory<Romd.Admin.Host.Program>(
                dataDirectory,
                database.ConnectionString);
            AnnounceWorkerHeartbeat(factory.Services);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");
            using var livenessResponse = await client.GetAsync("/health");

            readyResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            livenessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("status").GetString().ShouldBe("unready");
            GetCheckStatuses(body)["database"].ShouldBe("unready");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AdminHost_StaleUnprocessedOutboxRow_ReportsDegradedWithOutboxCheckDegraded()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));
            await SeedStaleUnprocessedOutboxRowAsync(database);

            await using var factory = CreateFactory<Romd.Admin.Host.Program>(
                dataDirectory,
                database.ConnectionString);
            AnnounceWorkerHeartbeat(factory.Services);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");

            readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("status").GetString().ShouldBe("degraded");
            GetCheckStatuses(body)["outbox"].ShouldBe("degraded");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConsumerHost_MapsReadinessWithOnlyDatabaseDiskAndStorageChecks()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));

            await using var factory = CreateFactory<Romd.Consumer.Host.Program>(
                dataDirectory,
                database.ConnectionString);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");

            readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("status").GetString().ShouldBe("ready");
            GetCheckStatuses(body).Keys.ShouldBe(
                ["database", "application-schema", "disk", "storage", "hangfire-storage"],
                ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadinessResponse_ContainsOnlyStatusAndCheckNameStatusFields()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));

            await using var factory = CreateFactory<Romd.Admin.Host.Program>(
                dataDirectory,
                database.ConnectionString);
            using var client = factory.CreateClient();

            using var readyResponse = await client.GetAsync("/health/ready");

            using var body = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ShouldBe(["checks", "status"]);
            var checks = body.RootElement.GetProperty("checks").EnumerateArray().ToArray();
            checks.ShouldNotBeEmpty();
            foreach (var check in checks)
            {
                check.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ShouldBe(["name", "status"]);
            }
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static Dictionary<string, string?> GetCheckStatuses(JsonDocument body) =>
        body.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check => check.GetProperty("status").GetString(),
                StringComparer.Ordinal);

    private static string CreateDataDirectory()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-readiness-{Guid.NewGuid():N}");
        string webRoot = Path.Combine(dataDirectory, "wwwroot");

        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        return dataDirectory;
    }

    private static async Task MakeMigrationsPendingAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {PostgreSqlConfiguration.SchemaName}.\"{PostgreSqlConfiguration.MigrationsHistoryTable}\"");
    }

    private static async Task SeedStaleUnprocessedOutboxRowAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        db.AdminRealtimeOutboxEvents.Add(new AdminRealtimeOutboxEventEntity
        {
            EventType = "JobUpdated",
            PayloadJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            AvailableAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();
    }

    private static void AnnounceWorkerHeartbeat(IServiceProvider services)
    {
        var storage = services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        connection.AnnounceServer(
            "readiness-test-worker",
            new ServerContext { Queues = ["default"], WorkerCount = 1 });
        connection.Heartbeat("readiness-test-worker");
    }

    private static WebApplicationFactory<TProgram> CreateFactory<TProgram>(
        string dataDirectory,
        string connectionString)
        where TProgram : class =>
        new WebApplicationFactory<TProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting(
                    $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                    connectionString);
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(Path.Combine(dataDirectory, "wwwroot"));
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IHangfireSchemaProbe>();
                    services.AddSingleton<IHangfireSchemaProbe>(new HealthyHangfireSchemaProbe());

                    if (typeof(TProgram) == typeof(Romd.Admin.Host.Program))
                    {
                        services.AddHangfire((_, config) => config
                            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                            .UseSimpleAssemblyNameTypeSerializer()
                            .UseRecommendedSerializerSettings()
                            .UseInMemoryStorage());
                    }
                });
            });

    private sealed class HealthyHangfireSchemaProbe : IHangfireSchemaProbe
    {
        public Task<int?> GetPublishedVersionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<int?>(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
    }
}
