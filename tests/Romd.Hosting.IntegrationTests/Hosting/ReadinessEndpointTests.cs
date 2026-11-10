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
using Romd.Application.Common.Content;
using Romd.Application.Common.Readiness;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Entities;
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

    // libchdr stays resident once loaded and refuses a second path, so every in-process host in this assembly verifies
    // this one copy. A test may change only its build record, and restores it.
    private static readonly Lazy<string> ResidentLibchdr = new(() =>
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "chd-resident-libchdr");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return NativeTestBuilds.CopyBuild(NativeTestBuilds.LibchdrDirectory, directory);
    });

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
                ["database", "application-schema", "disk", "storage", "native-media", "hangfire-storage", "outbox", "worker"],
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
    public async Task ConsumerHost_MapsReadinessIncludingVerifiedNativeMedia()
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
                ["database", "application-schema", "disk", "storage", "native-media", "hangfire-storage"],
                ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConsumerHost_InvalidatedNativeMedia_FailsReadinessUntilVerificationRestoresIt()
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));
            await using var factory = CreateFactory<Romd.Consumer.Host.Program>(dataDirectory, database.ConnectionString);
            using var client = factory.CreateClient();
            var runtime = factory.Services.GetRequiredService<ChdDeploymentRuntime>();
            runtime.IsReady.ShouldBeTrue();
            runtime.Invalidate();

            using var rejected = await client.GetAsync("/health/ready");
            rejected.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            rejectedBody.RootElement.GetProperty("status").GetString().ShouldBe("unready");
            GetCheckStatuses(rejectedBody)["native-media"].ShouldBe("unready");
            GetCheckStatuses(rejectedBody).Where(check => check.Key != "native-media")
                .ShouldAllBe(check => check.Value == "healthy");

            runtime.Verify();
            // Readiness intentionally caches evaluations for the production TTL.
            using var cached = await client.GetAsync("/health/ready");
            cached.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            await Task.Delay(factory.Services.GetRequiredService<ReadinessOptions>().ResultCacheTtl
                + TimeSpan.FromMilliseconds(100));
            using var restored = await client.GetAsync("/health/ready");
            restored.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var restoredBody = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
            GetCheckStatuses(restoredBody)["native-media"].ShouldBe("healthy");
        }
        finally { Directory.Delete(dataDirectory, recursive: true); }
    }

    [Fact]
    public async Task AdminHost_ChdOn_RefusesAnotherTupleAndFailedReverificationKeepsNativeMediaUnreadyUntilRestart() =>
        await AssertChdOnRefusalUntilRestartAsync<Romd.Admin.Host.Program>();

    [Fact]
    public async Task ConsumerHost_ChdOn_RefusesAnotherTupleAndFailedReverificationKeepsNativeMediaUnreadyUntilRestart() =>
        await AssertChdOnRefusalUntilRestartAsync<Romd.Consumer.Host.Program>();

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

    private static async Task AssertChdOnRefusalUntilRestartAsync<TProgram>() where TProgram : class
    {
        string dataDirectory = CreateDataDirectory();
        using var database = PostgreSqlTestDatabase.Create();
        string libchdr = ResidentLibchdr.Value;
        string record = Path.Combine(libchdr, ChdNativeCandidate.RecordFileName);
        byte[] verifiedRecord = await File.ReadAllBytesAsync(record);
        try
        {
            await database.ProvisionAsync();
            Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));
            SyntheticChd deployed;
            await using (var factory = CreateFactory<TProgram>(dataDirectory, database.ConnectionString, libchdr))
            {
                using var client = CreateReadyClient(factory);
                var runtime = factory.Services.GetRequiredService<ChdDeploymentRuntime>();
                var admission = factory.Services.GetRequiredService<IRomChdDeliveryAdmission>();
                var resident = runtime.Assets.ShouldNotBeNull().Native.ExpectedIdentity;
                var tuple = new RomChdDecoderBuild(resident.Rid, Sha1.Parse(resident.UpstreamCommit), resident.RecipeVersion);
                deployed = SyntheticChd.Create(tuple);
                runtime.IsReady.ShouldBeTrue();

                // Control: the host re-verifies its resident build and admits its own tuple for both formats.
                deployed.ShouldBeAdmitted(admission, true);
                // Retained evidence of another tuple is refused for both formats and leaves readiness alone.
                SyntheticChd.Create(new(tuple.Rid, tuple.UpstreamCommit, tuple.RecipeVersion + 1)).ShouldBeAdmitted(admission, false);
                runtime.IsReady.ShouldBeTrue();

                // The deployed record now names another tuple, so re-verification refuses and readiness fails closed.
                NativeTestBuilds.ChangeRecord(libchdr, node => node["recipeVersion"] = tuple.RecipeVersion + 1);
                deployed.ShouldBeAdmitted(admission, false);
                runtime.IsReady.ShouldBeFalse();
                await ShouldReportOnlyNativeMediaUnreadyAsync(client);

                // Restoring the record does not restore readiness: nothing re-verifies until the host restarts.
                await File.WriteAllBytesAsync(record, verifiedRecord);
                deployed.ShouldBeAdmitted(admission, false);
                await Task.Delay(factory.Services.GetRequiredService<ReadinessOptions>().ResultCacheTtl
                    + TimeSpan.FromMilliseconds(100));
                await ShouldReportOnlyNativeMediaUnreadyAsync(client);
            }

            await using var restarted = CreateFactory<TProgram>(dataDirectory, database.ConnectionString, libchdr);
            using var restartedClient = CreateReadyClient(restarted);
            using var ready = await restartedClient.GetAsync("/health/ready");
            ready.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
            GetCheckStatuses(body)["native-media"].ShouldBe("healthy");
            deployed.ShouldBeAdmitted(restarted.Services.GetRequiredService<IRomChdDeliveryAdmission>(), true);
        }
        finally
        {
            await File.WriteAllBytesAsync(record, verifiedRecord);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static HttpClient CreateReadyClient<TProgram>(WebApplicationFactory<TProgram> factory) where TProgram : class
    {
        var client = factory.CreateClient();
        if (typeof(TProgram) == typeof(Romd.Admin.Host.Program))
            AnnounceWorkerHeartbeat(factory.Services);
        return client;
    }

    private static async Task ShouldReportOnlyNativeMediaUnreadyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().ShouldBe("unready");
        var checks = GetCheckStatuses(body);
        checks["native-media"].ShouldBe("unready");
        checks.Where(check => check.Key != "native-media").ShouldAllBe(check => check.Value == "healthy");
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
        string connectionString,
        string? libchdrDirectory = null)
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
                if (libchdrDirectory is not null)
                    builder.UseSetting("Romd:Native:LibchdrDirectory", libchdrDirectory);

                builder.ConfigureTestServices(services =>
                {
                    foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
                        && service.ImplementationType != typeof(ChdAssetsStartupCheck)).ToArray())
                        services.Remove(service);
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

    /// <summary>Retained PS2 CHD facts measured by one decoder tuple; admission never opens their bytes.</summary>
    private sealed record SyntheticChd(RomContentDescriptor Content, RomContentArtifact Artifact, RomChdDeliveryEvidence Evidence)
    {
        public static SyntheticChd Create(RomChdDecoderBuild decoder)
        {
            var logical = new RomLogicalIdentity(8192, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('a', 40)),
                Md5.Parse(new string('a', 32)), Crc32.Parse("aaaaaaaa"));
            var overall = Sha1.Parse(new string('c', 40));
            var container = MediaFactFixtures.Chd(logical.Size, 4096, 2048, [0x7a6c6962, 0, 0, 0], logical.Sha1, overall,
                [new(0x44564420, 1, new byte[] { 0 })]);
            var measurement = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), Sha256.Parse(new string('b', 64)), 4096,
                logical, container, new RomDecoderIdentity.Native(decoder), "chd-logical/v1", "test-only-verifier",
                new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(42), logical.Sha1, overall);
            var stored = MediaDeliveryFactFixtures.MeasuredWhole(71, 12, 171, 7, true, container, measurement,
                Md5.Parse(new string('b', 32)), Sha1.Parse(new string('b', 40)));
            return new(new(7, stored.OutputId, logical, "disc.iso", [stored], [], []), stored, stored.ChdEvidence.Single());
        }

        /// <summary>Planning and every read of either format re-run the host's serving authority.</summary>
        public void ShouldBeAdmitted(IRomChdDeliveryAdmission admission, bool admitted)
        {
            RomDeliveryPlanner.Plan(Content, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, admission)
                .Where(option => option.Format is RomDeliveryFormat.Original or RomDeliveryFormat.Chd)
                .Select(option => option.State == RomDeliveryState.Available).ShouldBe([admitted, admitted]);
            foreach (var format in new[] { RomDeliveryFormat.Original, RomDeliveryFormat.Chd })
                Evidence.AdmitsTarget(Target(format), Content, Artifact, admission).ShouldBe(admitted, format.ToString());
        }

        private RomDeliveryTarget Target(RomDeliveryFormat format) => new(Content.RomId, Artifact.FileId, format,
            format == RomDeliveryFormat.Original ? "disc.iso" : "disc.chd",
            format == RomDeliveryFormat.Original ? Content.ContentSha256 : Artifact.ArtifactSha256,
            format == RomDeliveryFormat.Original ? Content.ContentSize : Artifact.ArtifactSize,
            Content.ContentSha256, Content.ContentSize, Artifact.ArtifactSha256, Artifact.ArtifactSize,
            RomRepresentationEncoding.Chd, format == RomDeliveryFormat.Original ? Content.ContentMd5 : Artifact.ArtifactMd5,
            format == RomDeliveryFormat.Original ? Content.ContentSha1 : Artifact.ArtifactSha1, Evidence.Bind());
    }
}
