using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class RomdPostgreSqlContainerTests
{
    [Fact]
    public async Task DisposeAsync_ReusedPublishedPort_IsolatesRolePoolsAndPreservesOtherFixtures()
    {
        await using var other = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        int otherBackend = await ReadBackendAsync(other.WorkerConnectionString);
        string[] previousConnectionStrings;
        int reusedPort;

        await using (var previous = await RomdPostgreSqlContainer.StartWithRomdRolesAsync())
        {
            previousConnectionStrings = AllConnectionStrings(previous);
            reusedPort = new NpgsqlConnectionStringBuilder(previous.BootstrapConnectionString).Port;
            await ProvisionAsync(previous.ProvisionerConnectionString);
            foreach (string connectionString in previousConnectionStrings)
            {
                int backend = await ReadBackendAsync(connectionString);
                (await ReadBackendAsync(connectionString)).ShouldBe(backend);
            }
        }

        await using var replacement = await RomdPostgreSqlContainer.StartWithRomdRolesAsync(reusedPort);
        var replacementSettings = new NpgsqlConnectionStringBuilder(replacement.BootstrapConnectionString);
        replacementSettings.Port.ShouldBe(reusedPort);
        replacementSettings.ApplicationName.ShouldNotBe(
            new NpgsqlConnectionStringBuilder(previousConnectionStrings[0]).ApplicationName);

        // Exercise the exact EF provisioning path that previously received the dead
        // server's 57P01 error on its first schema-history read after port reuse.
        await ProvisionAsync(replacement.ProvisionerConnectionString);
        foreach (string connectionString in AllConnectionStrings(replacement))
        {
            new NpgsqlConnectionStringBuilder(connectionString).ApplicationName
                .ShouldBe(replacementSettings.ApplicationName);
            int backend = await ReadBackendAsync(connectionString);
            (await ReadBackendAsync(connectionString)).ShouldBe(backend);
        }

        try
        {
            // Reopening the old exact keys proves disposal cleared every owned pool;
            // unique replacement identities alone would hide a missing cleanup step.
            await ProvisionAsync(previousConnectionStrings[1]);
            foreach (string connectionString in previousConnectionStrings)
                await ReadBackendAsync(connectionString);
        }
        finally
        {
            // The assertions deliberately reopened the previous fixture's keys against
            // its replacement. They remain this test's responsibility to clear.
            foreach (string connectionString in previousConnectionStrings)
            {
                using var connection = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(connection);
            }
        }

        (await ReadBackendAsync(other.WorkerConnectionString)).ShouldBe(otherBackend);
    }

    private static string[] AllConnectionStrings(RomdPostgreSqlContainer postgres) =>
    [postgres.BootstrapConnectionString, postgres.ProvisionerConnectionString, .. postgres.RuntimeConnectionStrings];

    private static async Task<int> ReadBackendAsync(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        settings.Pooling.ShouldBeTrue();
        settings.ApplicationName.ShouldNotBeNullOrWhiteSpace();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_backend_pid(), session_user, current_setting('application_name')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(1).ShouldBe(settings.Username);
        reader.GetString(2).ShouldBe(settings.ApplicationName);
        return reader.GetInt32(0);
    }

    private static Task ProvisionAsync(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = connectionString
        }).Build();
        return new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance)
            .StartAsync(default);
    }
}
