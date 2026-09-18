using System.Diagnostics;
using System.Net;
using Npgsql;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.RomdHostProcesses;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The fresh-install path a new deployment takes: no legacy store, no separate provisioning
///     step. The plain worker entrypoint must provision the Hangfire schema before any of its own
///     startup work (recurring-job registration, dispatchers, servers) touches storage, and both
///     API hosts must converge from fail-closed to ready.
/// </summary>
[Collection(nameof(HangfirePostgreSqlColdStartCollection))]
public sealed class HangfirePostgreSqlFreshInstallTests
{
    [Fact]
    public async Task NormalWorkerStart_ProvisionsSchemaBeforeAnyStorageUse_AndApiHostsConverge()
    {
        await using RomdPostgreSqlContainer postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        string dataDirectory = CreateDataDirectory("romd-pg-fresh-install");
        int adminPort = GetFreePort();
        int consumerPort = GetFreePort();
        using Process admin = StartAdmin(adminPort, dataDirectory, postgres.AdminConnectionString);
        using Process consumer = StartConsumer(consumerPort, dataDirectory, postgres.ConsumerConnectionString);
        Process? worker = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.ServiceUnavailable);
            await WaitForStatusAsync(client, consumerPort, "/health/ready", HttpStatusCode.ServiceUnavailable);

            worker = StartWorker(dataDirectory, postgres.WorkerConnectionString, postgres.ProvisionerConnectionString);

            await WaitForStatusAsync(client, adminPort, "/health/ready", HttpStatusCode.OK, TimeSpan.FromSeconds(60));
            await WaitForStatusAsync(client, consumerPort, "/health/ready", HttpStatusCode.OK, TimeSpan.FromSeconds(60));
            // Hangfire servers heartbeat under the runtime credential only once startup ran to completion.
            await WaitForReadinessCheckAsync(client, adminPort, "worker", "healthy", TimeSpan.FromSeconds(30));
            worker.HasExited.ShouldBeFalse(RecentOutput(worker));

            Convert.ToInt32(await ScalarAsync(postgres.AdminConnectionString, "SELECT version FROM hangfire.romd_schema"))
                .ShouldBe(1);
            Convert.ToInt32(await ScalarAsync(postgres.AdminConnectionString, "SELECT count(*) FROM hangfire.server"))
                .ShouldBeGreaterThan(0);
        }
        finally
        {
            Stop(admin);
            Stop(consumer);
            if (worker is not null) Stop(worker);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
