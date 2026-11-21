using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
    public async Task Start_MovesStoredSourcesAndSkipsAlreadyReviewedVersions()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string folder = fixture.ServerImportPath;
        string ready = Path.Combine(folder, "ready.bin");
        string temporary = Path.Combine(folder, "copy.bin.partial");
        string changed = Path.Combine(folder, "changed.bin");
        byte[] matched = RandomNumberGenerator.GetBytes(128);
        string catalogName = $"Drop folder {Guid.NewGuid():N}";
        await TestHelpers.UploadDatWithPlatformAsync(client, $"""
            <datafile><header><name>{catalogName}</name></header>
              <game name="{catalogName}">
                <rom name="ready.bin" size="{matched.Length}" sha1="{Convert.ToHexString(SHA1.HashData(matched))}"/>
              </game>
            </datafile>
            """, "drop-folder.dat", await TestHelpers.GetFirstSystemKeyAsync(client));
        await File.WriteAllBytesAsync(ready, matched);
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
        await WaitForSourceCleanupAsync(firstId);
        File.Exists(ready).ShouldBeFalse();
        File.Exists(temporary).ShouldBeTrue();
        File.Exists(changed).ShouldBeTrue();

        using var waitingResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        waitingResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var waiting = (await waitingResponse.Content.ReadFromJsonAsync<ServerImportStatus>()).ShouldNotBeNull();
        waiting.LastCheckOutcome.ShouldBe("WaitingForFiles");
        waiting.WaitingFiles.ShouldBe(2);
        waiting.LastJobId.ShouldBe(firstId);
        waiting.LastQueuedFiles.ShouldBe(1);

        string finished = Path.Combine(folder, "copy.bin");
        File.Move(temporary, finished);
        string completeArchive = Path.Combine(folder, "complete.zip");
        string partialArchive = Path.Combine(folder, "partial.zip");
        string unsupported = Path.Combine(folder, "unsupported.rar");
        await WriteZipAsync(completeArchive, ("nested/duplicate.bin", matched));
        await WriteZipAsync(partialArchive, ("duplicate.bin", matched), ("unmatched.bin", [101, 102, 103]));
        await File.WriteAllBytesAsync(unsupported, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0, 1]);
        File.SetLastWriteTimeUtc(completeArchive, DateTime.UnixEpoch.AddDays(2));
        File.SetLastWriteTimeUtc(partialArchive, DateTime.UnixEpoch.AddDays(2));
        File.SetLastWriteTimeUtc(unsupported, DateTime.UnixEpoch.AddDays(2));
        File.SetLastWriteTimeUtc(finished, DateTime.UnixEpoch.AddDays(2));
        File.SetLastWriteTimeUtc(changed, DateTime.UnixEpoch.AddDays(2));
        using var secondResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var second = (await secondResponse.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        await WaitForImportPhaseAsync(client, second.JobId, "CompletedWithErrors");
        await WaitForSourceCleanupAsync(second.JobId);
        File.Exists(completeArchive).ShouldBeFalse();
        File.Exists(partialArchive).ShouldBeTrue();
        File.Exists(unsupported).ShouldBeTrue();

        using var idleResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        idleResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var idle = (await idleResponse.Content.ReadFromJsonAsync<ServerImportStatus>()).ShouldNotBeNull();
        idle.LastCheckOutcome.ShouldBe("NoNewFiles");
        idle.LastJobId.ShouldBe(second.JobId);
        idle.LastQueuedFiles.ShouldBe(5);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var jobs = await db.Jobs.OfType<UploadJobEntity>().Where(job => job.ImportSourcePath == folder)
            .OrderBy(job => job.CreatedAt).ToListAsync();
        jobs.Select(job => job.RomsDiscovered).ShouldBe([1, 6]);
        jobs.ShouldAllBe(job => job.ImportMove);
        jobs[0].RomsIngested.ShouldBe(1);
        jobs[1].RomsDeduplicated.ShouldBe(2);
        (await File.ReadAllBytesAsync(finished)).ShouldBe([40, 50, 60]);

        string damaged = Path.Combine(folder, "damaged.zip");
        byte[] invalidZip = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02];
        await File.WriteAllBytesAsync(damaged, invalidZip);
        File.SetLastWriteTimeUtc(damaged, DateTime.UnixEpoch.AddDays(3));
        using var damagedResponse = await client.PostAsJsonAsync(_route, new StartServerImport());
        damagedResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var failed = (await damagedResponse.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        var failure = await WaitForImportPhaseAsync(client, failed.JobId, "Failed");
        failure.LastJobId.ShouldBe(failed.JobId);
        failure.CanRetry.ShouldBeTrue();
        using var staleRetry = await client.PostAsJsonAsync(_route, new StartServerImport(second.JobId));
        staleRetry.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var retry = await client.PostAsJsonAsync(_route, new StartServerImport(failed.JobId));
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var retried = (await retry.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        retried.JobId.ShouldNotBe(failed.JobId);
        var retriedFailure = await WaitForImportPhaseAsync(client, retried.JobId, "Failed");
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

    private async Task WaitForSourceCleanupAsync(Guid jobId)
    {
        string manifest = Path.Combine(fixture.DataDirectoryPath, "temp", "jobs", $"{jobId:N}.import.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (File.Exists(manifest))
            await Task.Delay(50, timeout.Token);
    }

    private static async Task WriteZipAsync(string path, params (string Name, byte[] Bytes)[] entries)
    {
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            await using var output = archive.CreateEntry(name).Open();
            await output.WriteAsync(bytes);
        }
    }

    private static async Task<ServerImportStatus> WaitForImportPhaseAsync(HttpClient client, Guid jobId, string phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = (await client.GetFromJsonAsync<ServerImportStatus>(_route, timeout.Token)).ShouldNotBeNull();
            if (status.LastJobId == jobId && !status.IsRunning)
            {
                status.LastJobPhase.ShouldBe(phase);
                return status;
            }
            await Task.Delay(50, timeout.Token);
        }
    }
}
