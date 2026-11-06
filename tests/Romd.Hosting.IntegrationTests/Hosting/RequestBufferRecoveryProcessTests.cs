using System.Diagnostics;
using System.Net;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Hosting.RomdHostProcesses;

namespace Romd.Hosting.IntegrationTests.Hosting;

[Collection(nameof(HangfirePostgreSqlColdStartCollection))]
public sealed class RequestBufferRecoveryProcessTests
{
    [Fact]
    public async Task AdminStart_LiveOwnerThenKilledOwner_RejectsOverlapAndRecoversBeforeListening()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        string dataDirectory = CreateDataDirectory("romd-request-buffer-recovery");
        string bufferDirectory = RequestBufferDirectory(dataDirectory);
        Directory.CreateDirectory(bufferDirectory);
        string orphan = Path.Combine(bufferDirectory, "ASPNETCORE_orphan.tmp");
        File.WriteAllText(orphan, "interrupted upload");
        int port = GetFreePort();
        using Process first = StartAdmin(port, dataDirectory, postgres.AdminConnectionString);
        Process? contender = null;
        Process? restarted = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await WaitForStatusAsync(client, port, "/health", HttpStatusCode.OK, TimeSpan.FromSeconds(30));
            File.Exists(orphan).ShouldBeFalse();
            // Model a request buffer left by a killed process without allocating a large upload.
            File.WriteAllText(orphan, "in-flight request");
            int contenderPort = GetFreePort();
            contender = StartAdmin(contenderPort, dataDirectory, postgres.AdminConnectionString);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await contender.WaitForExitAsync(budget.Token);
            contender.ExitCode.ShouldNotBe(0, RecentOutput(contender));
            RecentOutput(contender).ShouldContain("Cannot exclusively own ASPNETCORE_TEMP");
            File.Exists(orphan).ShouldBeTrue();
            await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{contenderPort}/health"));

            Stop(first); // SIGKILL: no hosted-service stop or disposal can release the lease.
            restarted = StartAdmin(port, dataDirectory, postgres.AdminConnectionString);
            await WaitForStatusAsync(client, port, "/health", HttpStatusCode.OK, TimeSpan.FromSeconds(30));
            File.Exists(orphan).ShouldBeFalse();
            File.Exists(Path.Combine(bufferDirectory, ".romd-request-buffers.lock")).ShouldBeTrue();
        }
        finally
        {
            Stop(first);
            if (contender is not null)
            { Stop(contender); contender.Dispose(); }
            if (restarted is not null)
            { Stop(restarted); restarted.Dispose(); }
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
