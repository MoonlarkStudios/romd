using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Host.Configuration;
using Romd.Infrastructure.Enrichment;
using Npgsql;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Search;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;
using static Romd.Hosting.IntegrationTests.Hosting.RomdHostProcesses;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The restore drill the ADR requires (#125, #128 Phase 4): scripts/backup/backup.sh captures one
///     quiesced backup set of a real worker-provisioned installation, restore.sh rebuilds it into an
///     empty PostgreSQL (roles from initdb) plus an empty data directory, the real worker and admin
///     host converge on it, identity/keys/content and search survive, and incomplete, mixed-generation,
///     or non-empty-destination restores are refused with distinct exit codes.
/// </summary>
[Collection(nameof(HangfirePostgreSqlColdStartCollection))]
public sealed class BackupSetRestoreDrillTests
{
    private const string AdminLogin = "admin@localhost";
    private const string AdminPassword = "ChangeMe123!";
    private const string DrillTitleName = "Drill Restore Title";
    private static readonly string BlobRelativePath = Path.Combine("content", "dr", "drill-blob.bin");
    // Not *.xml: the data-protection key ring parses every XML file in dp-keys on host start.
    private static readonly string KeyRingRelativePath = Path.Combine("dp-keys", "drill-marker.bin");

    [Fact]
    public async Task BackupSet_RestoredIntoEmptyInstallation_ConvergesAndRejectsMixedGenerations()
    {
        await using RomdPostgreSqlContainer source = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        string sourceData = CreateDataDirectory("romd-drill-source");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"romd-drill-backups-{Guid.NewGuid():N}");
        string targetData = Path.Combine(Path.GetTempPath(), $"romd-drill-target-{Guid.NewGuid():N}");
        Process? sourceWorker = null;
        Process? targetWorker = null;
        Process? targetAdmin = null;
        try
        {
            sourceWorker = StartWorker(
                sourceData,
                source.WorkerConnectionString,
                source.ProvisionerConnectionString);
            await WaitForProvisionedInstallationAsync(source.BootstrapConnectionString, sourceWorker);
            Stop(sourceWorker);
            await SeedDrillContentAsync(source.WorkerConnectionString, sourceData);
            InstallationFacts expected = await CaptureFactsAsync(source.BootstrapConnectionString, sourceData);

            ProcessResult backup = await RunBackupScriptAsync(
                "backup.sh",
                backupRoot,
                source.ContainerId,
                sourceData);
            backup.ExitCode.ShouldBe(0, backup.Output);
            string setDirectory = Directory.GetDirectories(backupRoot).ShouldHaveSingleItem();
            AssertManifest(setDirectory);

            await using RomdPostgreSqlContainer target = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
            ProcessResult restore = await RunBackupScriptAsync(
                "restore.sh",
                setDirectory,
                target.ContainerId,
                targetData);
            restore.ExitCode.ShouldBe(0, restore.Output);

            var monitoring = CreateRuntimeStorage(target.WorkerConnectionString).GetMonitoringApi();
            var restoredServers = monitoring.Servers().Select(server => server.Name).ToHashSet();
            int adminPort = GetFreePort();
            targetWorker = StartWorker(
                targetData,
                target.WorkerConnectionString,
                target.ProvisionerConnectionString);
            // A restored server heartbeat can still be healthy. Only a new registration proves
            // this worker has run the provisioners and seeders, including the runtime grant revokes.
            using (var startupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                while (!monitoring.Servers().Any(server => !restoredServers.Contains(server.Name)))
                {
                    targetWorker.HasExited.ShouldBeFalse(RecentOutput(targetWorker));
                    await Task.Delay(250, startupBudget.Token);
                }
            }
            targetAdmin = StartAdmin(adminPort, targetData, target.AdminConnectionString);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.OK, TimeSpan.FromSeconds(60));
            // Readiness now observes an installation whose new worker has completed provisioning.
            await WaitForReadinessCheckAsync(client, adminPort, "worker", "healthy", TimeSpan.FromSeconds(30));
            targetWorker.HasExited.ShouldBeFalse(RecentOutput(targetWorker));

            (await CaptureFactsAsync(target.BootstrapConnectionString, targetData)).ShouldBe(expected);
            const string adminSpaClientRow = """
                SELECT count(*) FROM romd."OpenIddictApplications" WHERE "ClientId" = 'romd-admin-spa'
                """;
            string[] readers = [target.BootstrapConnectionString, target.AdminConnectionString];
            foreach (string reader in readers)
            {
                Convert.ToInt64(await ScalarAsync(reader, adminSpaClientRow))
                    .ShouldBe(1, "the restored admin SPA client is visible to the bootstrap and admin roles");
            }
            await AssertOwnershipAndGrantsAsync(target);
            await AssertRestoredProviderCredentialsAsync(target.AdminConnectionString, targetData);
            await AssertRestoredProviderCredentialsAsync(target.WorkerConnectionString, targetData);
            await AssertRestoredAdminCanLogInAndSearchAsync(adminPort, targetAdmin);

            // Refusals: a second restore meets a populated destination; a tampered artifact and a
            // missing artifact are incomplete or mixed-generation sets and never reach the destination.
            ProcessResult again = await RunBackupScriptAsync(
                "restore.sh",
                setDirectory,
                target.ContainerId,
                targetData);
            again.ExitCode.ShouldBe(3, again.Output);
            again.Output.ShouldContain("already holds");

            string tampered = CopySet(setDirectory, "tampered");
            await TamperAsync(Path.Combine(tampered, "data.tar"));
            ProcessResult mixed = await RunBackupScriptAsync(
                "restore.sh",
                tampered,
                target.ContainerId,
                targetData);
            mixed.ExitCode.ShouldBe(2, mixed.Output);
            mixed.Output.ShouldContain("mixed-generation");

            string incomplete = CopySet(setDirectory, "incomplete");
            File.Delete(Path.Combine(incomplete, "roles.sh"));
            ProcessResult missing = await RunBackupScriptAsync(
                "restore.sh",
                incomplete,
                target.ContainerId,
                targetData);
            missing.ExitCode.ShouldBe(2, missing.Output);
            missing.Output.ShouldContain("missing roles.sh");
        }
        finally
        {
            if (sourceWorker is not null) Stop(sourceWorker);
            if (targetAdmin is not null) Stop(targetAdmin);
            if (targetWorker is not null) Stop(targetWorker);
            foreach (string directory in new[] { sourceData, backupRoot, targetData })
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    ///     Waits until the worker finished starting: the Hangfire servers register only after the
    ///     provisioner and every seeder (admin user, platforms, OIDC clients) ran.
    /// </summary>
    private static async Task WaitForProvisionedInstallationAsync(string bootstrapConnectionString, Process worker)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            worker.HasExited.ShouldBeFalse(
                $"the worker exited before provisioning:{Environment.NewLine}{RecentOutput(worker)}");
            try
            {
                bool started = Convert.ToBoolean(await ScalarAsync(
                    bootstrapConnectionString,
                    """
                    SELECT (SELECT count(*) FROM hangfire.server) > 0
                       AND (SELECT count(*) FROM romd."OpenIddictApplications") > 0
                       AND (SELECT count(*) FROM romd."AspNetUsers") > 0
                    """));
                if (started) return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
            {
            }

            await Task.Delay(250, budget.Token);
        }
    }

    private static async Task SeedDrillContentAsync(string connectionString, string dataDirectory)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>().AddInterceptors(new SearchDocumentInterceptor());
        PostgreSqlConfiguration.Configure(builder, connectionString);
        await using var db = new RomdDbContext(builder.Options);
        int platformId = await db.Platforms
            .OrderBy(platform => platform.Id)
            .Select(platform => platform.Id)
            .FirstAsync();
        db.Titles.Add(new TitleEntity
        {
            PlatformId = platformId,
            Name = DrillTitleName,
            NormalizedName = DrillTitleName.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        using (var services = CreateProviderServices(dataDirectory))
        {
            var settings = CreateProviderSettings(db, services);
            var saved = await settings.UpdateAsync(new UpdateIgdbProviderSettingsRequest(
                false, "drill-igdb-client", "drill-igdb-secret") { Revision = (await settings.GetAsync()).Revision });
            saved.IsError.ShouldBeFalse();
            saved.Value.IsConfigured.ShouldBeTrue();
            string? ciphertext = await db.Set<MetadataProviderSettingsEntity>()
                .Where(x => x.ProviderId == "igdb").Select(x => x.ProtectedClientSecret).SingleAsync();
            ciphertext.ShouldNotBeNullOrEmpty();
            ciphertext.ShouldNotContain("drill-igdb-secret");
        }

        // The real key ring above protects provider credentials; these markers check file integrity.
        foreach (string relativePath in new[] { BlobRelativePath, KeyRingRelativePath })
        {
            string path = Path.Combine(dataDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(4096));
        }
    }

    private static async Task AssertRestoredProviderCredentialsAsync(string connectionString, string dataDirectory)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(builder, connectionString);
        await using var db = new RomdDbContext(builder.Options);
        using var services = CreateProviderServices(dataDirectory);

        var settings = await CreateProviderSettings(db, services).GetAsync();

        settings.ClientId.ShouldBe("drill-igdb-client");
        settings.HasClientSecret.ShouldBeTrue();
        settings.IsConfigured.ShouldBeTrue("the restored database secret must decrypt using the restored key ring");
        settings.ConfigurationError.ShouldBeNull();
        settings.Enabled.ShouldBeFalse();
    }

    private static ServiceProvider CreateProviderServices(string dataDirectory) => new ServiceCollection()
        .AddLogging()
        .AddHttpClient()
        .AddRomdDataProtection(new RomdOptions { DataDirectory = dataDirectory })
        .BuildServiceProvider();

    private static IgdbProviderSettingsService CreateProviderSettings(RomdDbContext db, IServiceProvider services) => new(
        new IgdbProviderSettingsStore(db),
        services.GetRequiredService<IDataProtectionProvider>(),
        Options.Create(new IgdbProviderOptions()),
        Options.Create(new EnrichmentOptions()),
        services.GetRequiredService<IHttpClientFactory>(),
        TimeProvider.System);

    private static async Task<InstallationFacts> CaptureFactsAsync(string connectionString, string dataDirectory)
    {
        static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new InstallationFacts(
            await CountAsync(connectionString, "Titles"),
            await CountAsync(connectionString, "Platforms"),
            await CountAsync(connectionString, "AspNetUsers"),
            await CountAsync(connectionString, "OpenIddictApplications"),
            await CountAsync(connectionString, "__EFMigrationsHistory"),
            await PublishedVersionAsync(connectionString, "romd"),
            await PublishedVersionAsync(connectionString, "hangfire"),
            HashOf(Path.Combine(dataDirectory, ServerInstanceIdentity.RelativePath)),
            HashOf(Path.Combine(dataDirectory, "keys", "openiddict-signing.pem")),
            HashOf(Path.Combine(dataDirectory, BlobRelativePath)),
            HashOf(Path.Combine(dataDirectory, KeyRingRelativePath)));
    }

    private static async Task<int> PublishedVersionAsync(string connectionString, string schema) =>
        Convert.ToInt32(await ScalarAsync(
            connectionString,
            $"SELECT version FROM {schema}.romd_schema WHERE singleton"));

    private static async Task<long> CountAsync(string connectionString, string table) =>
        Convert.ToInt64(await ScalarAsync(connectionString, $"SELECT count(*) FROM romd.\"{table}\""));

    private static void AssertManifest(string setDirectory)
    {
        foreach (string artifact in new[] { "manifest.json", "roles.sh", "romd.dump", "data.tar" })
        {
            File.Exists(Path.Combine(setDirectory, artifact)).ShouldBeTrue(artifact);
        }

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(setDirectory, "manifest.json")));
        manifest.RootElement.GetProperty("format").GetInt32().ShouldBe(1);
        manifest.RootElement.GetProperty("backupSetId").GetString().ShouldBe(Path.GetFileName(setDirectory));
        manifest.RootElement.GetProperty("romdSchemaVersion").GetInt32()
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
        manifest.RootElement.GetProperty("hangfireSchemaVersion").GetInt32()
            .ShouldBe(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
        JsonElement artifacts = manifest.RootElement.GetProperty("artifacts");
        foreach (string artifact in new[] { "roles.sh", "romd.dump", "data.tar" })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(setDirectory, artifact));
            artifacts.GetProperty(artifact).GetProperty("sha256").GetString()
                .ShouldBe(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            artifacts.GetProperty(artifact).GetProperty("bytes").GetInt64().ShouldBe(bytes.LongLength);
        }
    }

    private static async Task AssertOwnershipAndGrantsAsync(RomdPostgreSqlContainer target)
    {
        string owners = Convert.ToString(await ScalarAsync(
            target.BootstrapConnectionString,
            """
            SELECT string_agg(DISTINCT pg_get_userbyid(c.relowner), ',')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('romd', 'hangfire') AND c.relkind IN ('r', 'S', 'i')
            """))!;
        owners.ShouldBe("romd_owner");

        // The worker's provisioner re-applied the exact grants on start: the consumer reads but
        // cannot write the readiness gate, and every runtime role is denied the schema version row.
        Convert.ToInt32(await ScalarAsync(
                target.ConsumerConnectionString,
                "SELECT version FROM romd.romd_schema WHERE singleton"))
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
        foreach (string runtime in target.RuntimeConnectionStrings)
        {
            PostgresException denied = await Should.ThrowAsync<PostgresException>(
                () => ScalarAsync(runtime, "UPDATE romd.romd_schema SET version = 99"));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static async Task AssertRestoredAdminCanLogInAndSearchAsync(int adminPort, Process admin)
    {
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{adminPort}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        Dictionary<string, JsonElement> tokens;
        try
        {
            tokens = await OidcAuthorizationCodeFlow.RunAsync(
                browser,
                RomdOpenIddictClients.AdminSpa,
                "http://localhost:5137/auth/callback",
                AdminLogin,
                AdminPassword);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Interactive login failed on the restored admin host:{Environment.NewLine}{RecentOutput(admin)}",
                exception);
        }
        string accessToken = tokens.TryGetValue("access_token", out JsonElement token) ? token.GetString()! : "";
        accessToken.ShouldNotBeNullOrWhiteSpace(string.Join(",", tokens.Keys));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog?query=drill");
        request.Headers.Authorization = new("Bearer", accessToken);
        using HttpResponseMessage response = await browser.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ShouldContain(DrillTitleName);
    }

    private static async Task<ProcessResult> RunBackupScriptAsync(
        string script,
        string argument,
        string containerId,
        string dataDirectory)
    {
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", "backup", script));
        start.ArgumentList.Add(argument);
        start.Environment["ROMD_BACKUP_MODE"] = "local";
        start.Environment["ROMD_BACKUP_PG_EXEC"] = $"docker exec -i {containerId}";
        start.Environment["ROMD_BACKUP_DATA_DIR"] = dataDirectory;
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start scripts/backup/{script}.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return new ProcessResult(process.ExitCode, $"{await standardOutput}\n{await standardError}".Trim());
    }

    private static string CopySet(string setDirectory, string suffix)
    {
        string copy = Path.Combine(
            Path.GetDirectoryName(setDirectory)!,
            $"{Path.GetFileName(setDirectory)}-{suffix}");
        Directory.CreateDirectory(copy);
        foreach (string file in Directory.GetFiles(setDirectory))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        return copy;
    }

    private static async Task TamperAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed record InstallationFacts(
        long Titles,
        long Platforms,
        long Users,
        long OpenIddictApplications,
        long AppliedMigrations,
        int RomdSchemaVersion,
        int HangfireSchemaVersion,
        string ServerIdentityHash,
        string SigningKeyHash,
        string DrillBlobHash,
        string KeyRingHash);
}
