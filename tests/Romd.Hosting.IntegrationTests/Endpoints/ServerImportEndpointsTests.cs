using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Commands;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ServerImportEndpointsTests(IntegrationTestFixture fixture)
{
    private const string _route = "/api/upload/server-folder";

    [Fact]
    public async Task Start_ReadyAndArrivingFiles_QueuesEachVersionOnceAndKeepsTheOriginals()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string folder = fixture.ServerImportPath;
        string ready = Path.Combine(folder, "ready.bin");
        string temporary = Path.Combine(folder, "copy.bin.partial");
        string changed = Path.Combine(folder, "changed.bin");
        await File.WriteAllBytesAsync(ready, [10, 20, 30]);
        await File.WriteAllBytesAsync(temporary, [40, 50, 60]);
        await File.WriteAllBytesAsync(changed, [70, 80, 90]);
        File.SetLastWriteTimeUtc(ready, DateTime.UnixEpoch.AddDays(1));

        var before = (await client.GetFromJsonAsync<ServerImportStatus>(_route)).ShouldNotBeNull();
        before.Directory.ShouldBe(folder);
        before.IntervalMinutes.ShouldBeNull();

        var requests = await Task.WhenAll(
            client.PostAsJsonAsync(_route, new StartServerImport()),
            client.PostAsJsonAsync(_route, new StartServerImport()));
        Guid firstId;
        using (requests[0])
        using (requests[1])
        {
            var accepted = new List<Guid>();
            foreach (var response in requests)
            {
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    var job = (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
                    response.Headers.Location?.OriginalString.ShouldBe($"/api/jobs/{job.JobId}");
                    accepted.Add(job.JobId);
                }
                else
                {
                    response.StatusCode.ShouldBe(HttpStatusCode.OK);
                }
            }
            firstId = accepted.Distinct().ShouldHaveSingleItem();
        }
        await TestHelpers.WaitForJobCompletionAsync(client, firstId.ToString());

        using var waitingResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        waitingResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var waiting = (await waitingResponse.Content.ReadFromJsonAsync<ServerImportStatus>()).ShouldNotBeNull();
        waiting.LastCheckOutcome.ShouldBe("WaitingForFiles");
        waiting.WaitingFiles.ShouldBe(2);
        waiting.LastJobId.ShouldBe(firstId);
        waiting.LastQueuedFiles.ShouldBe(1);

        string finished = Path.Combine(folder, "copy.bin");
        File.Move(temporary, finished);
        File.SetLastWriteTimeUtc(finished, DateTime.UnixEpoch.AddDays(2));
        File.SetLastWriteTimeUtc(changed, DateTime.UnixEpoch.AddDays(2));
        using var secondResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var second = (await secondResponse.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        await TestHelpers.WaitForJobCompletionAsync(client, second.JobId.ToString());

        using var idleResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        idleResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var idle = (await idleResponse.Content.ReadFromJsonAsync<ServerImportStatus>()).ShouldNotBeNull();
        idle.LastCheckOutcome.ShouldBe("NoNewFiles");
        idle.LastJobId.ShouldBe(second.JobId);
        idle.LastQueuedFiles.ShouldBe(2);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var jobs = await db.Jobs.OfType<UploadJobEntity>().Where(job => job.ImportSourcePath == folder)
            .OrderBy(job => job.CreatedAt).ToListAsync();
        jobs.Select(job => job.RomsDiscovered).ShouldBe([1, 2]);
        jobs.ShouldAllBe(job => !job.ImportMove);
        (await File.ReadAllBytesAsync(ready)).ShouldBe([10, 20, 30]);
        (await File.ReadAllBytesAsync(finished)).ShouldBe([40, 50, 60]);

        string damaged = Path.Combine(folder, "damaged.zip");
        byte[] invalidZip = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02];
        await File.WriteAllBytesAsync(damaged, invalidZip);
        File.SetLastWriteTimeUtc(damaged, DateTime.UnixEpoch.AddDays(3));
        using var damagedResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        damagedResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var failed = (await damagedResponse.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        var failure = await WaitForFailedImportAsync(client, failed.JobId);
        failure.LastJobId.ShouldBe(failed.JobId);
        failure.CanRetry.ShouldBeTrue();
        using var staleRetry = await client.PostAsJsonAsync(_route, new StartServerImport(second.JobId));
        staleRetry.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var retry = await client.PostAsJsonAsync(_route, new StartServerImport(failed.JobId));
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var retried = (await retry.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        retried.JobId.ShouldNotBe(failed.JobId);
        var retriedFailure = await WaitForFailedImportAsync(client, retried.JobId);
        retriedFailure.LastQueuedFiles.ShouldBe(1);
        (await File.ReadAllBytesAsync(damaged)).ShouldBe(invalidZip);

    }

    [Fact]
    public async Task ServerFolder_Contributor_CannotReadOrStartTheImport()
    {
        using var client = fixture.CreateClient()
            .WithTestUser(Guid.NewGuid(), "contributor@localhost", [RomdRoleType.Contributor]);
        using var status = await client.GetAsync(_route);
        using var start = await client.PostAsJsonAsync(_route, new StartServerImport());

        status.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        start.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
    private static async Task<ServerImportStatus> WaitForFailedImportAsync(HttpClient client, Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = (await client.GetFromJsonAsync<ServerImportStatus>(_route, timeout.Token)).ShouldNotBeNull();
            if (status.LastJobId == jobId && !status.IsRunning)
            {
                status.LastJobPhase.ShouldBe("Failed");
                return status;
            }
            await Task.Delay(50, timeout.Token);
        }
    }
}
