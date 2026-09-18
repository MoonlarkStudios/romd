using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Readiness;
using Romd.Persistence;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Runs deploy/postgres/init/10-romd-roles.sh in a real PostgreSQL 18 and proves the grant
///     model behaviourally: the worker provisions both schemas as romd_provisioner with romd_owner
///     owning every object, runtime roles get DML only and none of them can rewrite a published
///     schema version or the migration history, the consumer can read nothing but the published
///     Hangfire schema version, holds the exact application allow-list (#128), and an admin
///     enqueue is executed by a worker under the runtime credentials.
/// </summary>
public sealed class PostgreSqlRolePrivilegeTests
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task RoleBootstrap_ProvisionerOwnsSchema_RuntimeRolesAreDmlOnly_AdminEnqueueRunsOnWorker()
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();

        await ProvisionAsync(postgres.ProvisionerConnectionString);

        await AssertHangfireObjectsOwnedByRomdOwnerAsync(postgres.BootstrapConnectionString);
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            await AssertCannotCreateAlterOrDropAsync(runtime);
        }

        await AssertConsumerReadsPublishedVersionOnlyAsync(postgres);
        await AssertRomdSchemaDefaultsKeepConsumerReadOnlyAsync(postgres);
        await AssertReadinessProbeSeesPublishedVersionAsync(postgres.AdminConnectionString);
        await AssertReadinessProbeSeesPublishedVersionAsync(postgres.ConsumerConnectionString);
        await AssertAdminEnqueueIsExecutedByWorkerAsync(postgres);

        await ProvisionApplicationSchemaAsync(postgres.ProvisionerConnectionString);
        await AssertApplicationObjectsOwnedByRomdOwnerAsync(postgres.BootstrapConnectionString);
        await AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(postgres);
        await AssertWorkerAndAdminHaveApplicationDmlAsync(postgres);
        await AssertConsumerHoldsExactApplicationAllowListAsync(postgres.ConsumerConnectionString);
        await AssertMetadataProviderSettingsAreManagementOnlyAsync(postgres);
    }

    private static async Task ProvisionApplicationSchemaAsync(string provisionerConnectionString)
    {
        var provisioner = new PostgreSqlSchemaProvisioner(
            ConnectionConfiguration(
                PostgreSqlConfiguration.ProvisioningConnectionName,
                provisionerConnectionString),
            NullLogger<PostgreSqlSchemaProvisioner>.Instance);
        await provisioner.StartAsync(CancellationToken.None);
    }

    private static async Task AssertApplicationObjectsOwnedByRomdOwnerAsync(string bootstrapConnectionString)
    {
        await using var connection = new NpgsqlConnection(bootstrapConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'romd' AND c.relkind IN ('r', 'S', 'i')
            """;
        var owners = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                owners[reader.GetString(0)] = reader.GetString(1);
            }
        }

        owners.Keys.ShouldContain("Titles");
        owners.Keys.ShouldContain("__EFMigrationsHistory");
        owners.Keys.ShouldContain(PostgreSqlConfiguration.SchemaVersionTable);
        owners.Values.Distinct().ShouldBe(["romd_owner"]);
    }

    private static async Task AssertApplicationSchemaVersionReadableByEveryRuntimeRoleAsync(
        RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            Convert.ToInt32(await ScalarAsync(runtime, "SELECT version FROM romd.romd_schema WHERE singleton = TRUE"))
                .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
            Convert.ToInt32(await ScalarAsync(runtime, @"SELECT count(*) FROM romd.""__EFMigrationsHistory"""))
                .ShouldBe(typeof(RomdDbContext).Assembly.GetTypes().Count(type =>
                    type.IsSubclassOf(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration))));
            await AssertDeniedAsync(runtime, "UPDATE romd.romd_schema SET version = 99");
            await AssertDeniedAsync(
                runtime,
                "INSERT INTO romd.romd_schema (singleton, version, published_at_utc) VALUES (FALSE, 99, now())");
            await AssertDeniedAsync(runtime, @"DELETE FROM romd.""__EFMigrationsHistory""");
        }

        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(builder, postgres.ConsumerConnectionString);
        await using var context = new RomdDbContext(builder.Options);
        (await new RomdSchemaProbe(context).GetPublishedVersionAsync(CancellationToken.None))
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    private static async Task AssertWorkerAndAdminHaveApplicationDmlAsync(RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in new[] { postgres.WorkerConnectionString, postgres.AdminConnectionString })
        {
            await ExecuteAsync(runtime, """
                INSERT INTO romd."Regions" ("Name", "SortOrder", "IsAutoCreated", "CreatedAt", "CreatedByUserId")
                VALUES ('Privilege probe', 999, FALSE, 0, '00000000-0000-0000-0000-000000000000')
                """);
            await ExecuteAsync(
                runtime,
                @"UPDATE romd.""Regions"" SET ""SortOrder"" = 998 WHERE ""Name"" = 'Privilege probe'");
            await ExecuteAsync(runtime, @"DELETE FROM romd.""Regions"" WHERE ""Name"" = 'Privilege probe'");
            await AssertDeniedAsync(runtime, @"TRUNCATE romd.""Regions""");
        }
    }

    /// <summary>
    ///     The consumer allow-list is exact: read catalog data, write its own settings, play sessions,
    ///     and OpenIddict grants, and touch only the credential columns of a user row. Anything else,
    ///     including the columns next to the granted ones, is a privilege error rather than a no-op.
    /// </summary>
    private static async Task AssertConsumerHoldsExactApplicationAllowListAsync(string consumerConnectionString)
    {
        const string noUser = "'00000000-0000-0000-0000-000000000000'";
        Convert.ToInt32(await ScalarAsync(consumerConnectionString, @"SELECT count(*) FROM romd.""Titles"""))
            .ShouldBe(0);
        await ExecuteAsync(consumerConnectionString, @"DELETE FROM romd.""OpenIddictTokens"" WHERE ""Id"" = 'none'");
        await ExecuteAsync(
            consumerConnectionString,
            @"DELETE FROM romd.""OpenIddictAuthorizations"" WHERE ""Id"" = 'none'");
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""ConsumerUserSettings"" SET ""UpdatedAt"" = 0 WHERE ""UserId"" = {noUser}");
        Convert.ToBoolean(await ScalarAsync(
                consumerConnectionString,
                """
                SELECT has_table_privilege(current_user, 'romd."PlaySessions"', 'INSERT')
                   AND has_table_privilege(current_user, 'romd."PlaySessions"', 'UPDATE')
                   AND has_table_privilege(current_user, 'romd."PlaySessions"', 'DELETE')
                """))
            .ShouldBeTrue();
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""PlaySessions"" SET ""UpdatedAt"" = 0 WHERE ""UserId"" = {noUser}");
        await ExecuteAsync(
            consumerConnectionString,
            $@"DELETE FROM romd.""PlaySessions"" WHERE ""UserId"" = {noUser}");
        await ExecuteAsync(
            consumerConnectionString,
            $@"UPDATE romd.""AspNetUsers"" SET ""SecurityStamp"" = 'probe' WHERE ""Id"" = {noUser}");

        await ExecuteAsync(consumerConnectionString,
            $@"UPDATE romd.""AspNetUsers"" SET ""LastSignedInAt"" = 0 WHERE ""Id"" = {noUser}");

        string[] denied =
        [
            @"SELECT * FROM romd.""AccountLinks""",
            @"SELECT * FROM romd.""AdminAuditEvents""",
            """
            INSERT INTO romd."Regions" ("Name", "SortOrder", "IsAutoCreated", "CreatedAt", "CreatedByUserId")
            VALUES ('x', 1, FALSE, 0, '00000000-0000-0000-0000-000000000000')
            """,
            @"UPDATE romd.""Titles"" SET ""Name"" = 'probe'",
            @"DELETE FROM romd.""ConsumerUserSettings""",
            @"DELETE FROM romd.""AspNetUsers""",
            $@"UPDATE romd.""AspNetUsers"" SET ""UserName"" = 'probe' WHERE ""Id"" = {noUser}",
            $@"UPDATE romd.""AspNetUsers"" SET ""Email"" = 'probe' WHERE ""Id"" = {noUser}",
            @"INSERT INTO romd.""OpenIddictApplications"" (""Id"", ""ConcurrencyToken"") VALUES ('x', 'x')",
            @"DELETE FROM romd.""Jobs"""
        ];
        foreach (string statement in denied)
        {
            await AssertDeniedAsync(consumerConnectionString, statement);
        }
    }

    private static async Task AssertMetadataProviderSettingsAreManagementOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        foreach (string runtime in new[] { postgres.AdminConnectionString, postgres.WorkerConnectionString })
        {
            Convert.ToInt32(await ScalarAsync(runtime,
                """SELECT count(*) FROM romd."MetadataProviderSettings" WHERE "ProviderId" = 'igdb'"""))
                .ShouldBe(1);
            await ExecuteAsync(runtime,
                """UPDATE romd."MetadataProviderSettings" SET "Enabled" = FALSE WHERE "ProviderId" = 'igdb'""");
        }

        string[] denied =
        [
            @"SELECT * FROM romd.""MetadataProviderSettings""",
            """INSERT INTO romd."MetadataProviderSettings" ("ProviderId") VALUES ('forbidden')""",
            """UPDATE romd."MetadataProviderSettings" SET "Enabled" = TRUE WHERE "ProviderId" = 'igdb'""",
            """DELETE FROM romd."MetadataProviderSettings" WHERE "ProviderId" = 'igdb'"""
        ];
        foreach (string statement in denied)
        {
            await AssertDeniedAsync(postgres.ConsumerConnectionString, statement);
        }
    }

    private static async Task ProvisionAsync(string provisionerConnectionString)
    {
        var provisioner = new HangfireSchemaProvisioner(
            ConnectionConfiguration(
                HangfirePostgreSqlConfiguration.ProvisioningConnectionName,
                provisionerConnectionString),
            NullLogger<HangfireSchemaProvisioner>.Instance);

        await provisioner.StartAsync(CancellationToken.None);
    }

    private static async Task AssertHangfireObjectsOwnedByRomdOwnerAsync(string bootstrapConnectionString)
    {
        await using var connection = new NpgsqlConnection(bootstrapConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'hangfire' AND c.relkind IN ('r', 'S')
            UNION ALL
            SELECT 'schema:hangfire', pg_get_userbyid(nspowner)
            FROM pg_namespace
            WHERE nspname = 'hangfire'
            """;
        var owners = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                owners[reader.GetString(0)] = reader.GetString(1);
            }
        }

        owners.Keys.ShouldContain("schema:hangfire");
        owners.Keys.ShouldContain("job");
        owners.Keys.ShouldContain("romd_schema");
        owners.Values.Distinct().ShouldBe(["romd_owner"]);
    }

    private static async Task AssertCannotCreateAlterOrDropAsync(string runtimeConnectionString)
    {
        string[] statements =
        [
            "CREATE TABLE hangfire.privilege_probe (id integer)",
            "ALTER TABLE hangfire.job ADD COLUMN privilege_probe integer",
            "DROP TABLE hangfire.romd_schema",
            "CREATE SCHEMA privilege_probe",
            "CREATE TABLE romd.privilege_probe (id integer)",
            "CREATE TABLE public.privilege_probe (id integer)"
        ];
        foreach (string statement in statements)
        {
            await AssertDeniedAsync(runtimeConnectionString, statement);
        }
    }

    private static async Task AssertConsumerReadsPublishedVersionOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        Convert.ToInt32(await ScalarAsync(postgres.ConsumerConnectionString, "SELECT version FROM hangfire.romd_schema"))
            .ShouldBe(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
        foreach (string runtime in postgres.RuntimeConnectionStrings)
        {
            await AssertDeniedAsync(runtime, "UPDATE hangfire.romd_schema SET version = 99");
        }
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "SELECT count(*) FROM hangfire.job");
    }

    private static async Task AssertRomdSchemaDefaultsKeepConsumerReadOnlyAsync(RomdPostgreSqlContainer postgres)
    {
        await ExecuteAsync(postgres.ProvisionerConnectionString, "CREATE TABLE romd.privilege_probe (id integer PRIMARY KEY)");
        await ExecuteAsync(postgres.WorkerConnectionString, "INSERT INTO romd.privilege_probe (id) VALUES (1)");
        await ExecuteAsync(postgres.AdminConnectionString, "UPDATE romd.privilege_probe SET id = 2");
        Convert.ToInt32(await ScalarAsync(postgres.ConsumerConnectionString, "SELECT count(*) FROM romd.privilege_probe"))
            .ShouldBe(1);
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "INSERT INTO romd.privilege_probe (id) VALUES (3)");
        await AssertDeniedAsync(postgres.ConsumerConnectionString, "DELETE FROM romd.privilege_probe");
    }

    private static async Task AssertReadinessProbeSeesPublishedVersionAsync(string runtimeConnectionString)
    {
        var probe = new PostgreSqlHangfireSchemaProbe(
            ConnectionConfiguration(HangfirePostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString));

        (await probe.GetPublishedVersionAsync(CancellationToken.None))
            .ShouldBe(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    private static async Task AssertAdminEnqueueIsExecutedByWorkerAsync(RomdPostgreSqlContainer postgres)
    {
        PostgreSqlStorage workerStorage = CreateRuntimeStorage(postgres.WorkerConnectionString);
        using var server = new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = 1,
                Queues = ["default"],
                ServerName = "role-privilege-worker",
                // Other integration tests replace JobActivator.Current with DI-bound activators
                // whose providers are disposed by the time this test runs.
                Activator = new JobActivator()
            },
            workerStorage);
        string jobId = new BackgroundJobClient(CreateRuntimeStorage(postgres.AdminConnectionString))
            .Enqueue(() => RolePrivilegeJobs.RunUnderWorkerRole());

        await WaitForLatestStateAsync(workerStorage, jobId, "Succeeded");
    }

    private static IConfiguration ConnectionConfiguration(string connectionName, string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{connectionName}"] = connectionString
            })
            .Build();

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        denied.SqlState.ShouldBe(InsufficientPrivilege, sql);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}

public static class RolePrivilegeJobs
{
    [AutomaticRetry(Attempts = 0)]
    public static void RunUnderWorkerRole()
    {
    }
}
