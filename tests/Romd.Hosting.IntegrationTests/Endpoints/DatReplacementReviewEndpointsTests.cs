using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class DatReplacementReviewEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ReviewAndApply_PreviewIsIsolated_ThenExactCandidateReplacesActiveVersion()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string name = $"Review {Guid.NewGuid():N}";
        string Document(string entries) => $"<datafile><header><name>{name}</name></header>{entries}</datafile>";
        const string first = "<game name='First'><rom name='first.bin' size='10' crc='11111111'/></game>";
        const string second = "<game name='Second'><rom name='second.bin' size='20' crc='22222222'/></game>";
        using var initial = TestHelpers.CreateMultipartContent(Document(first), "first.dat");
        var upload = await client.PostAsync("/api/upload/dat", initial);
        upload.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var accepted = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.RootElement.GetProperty("jobId").GetString()!);
        string id = await TestHelpers.GetDatIdByNameAsync(client, name);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var original = await db.DatFiles.SingleAsync(d => d.Name == name);
        int versions = await db.DatFiles.CountAsync();
        int claims = await db.SourceEntries.CountAsync();
        int jobs = await db.Jobs.CountAsync();
        using var candidate = TestHelpers.CreateMultipartContent(Document(first + second), "next.dat");
        var preview = await client.PostAsync($"/api/dats/{id}/replacement-preview", candidate);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var diff = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        diff.RootElement.GetProperty("entriesAdded").GetInt32().ShouldBe(1);
        (await db.DatFiles.CountAsync()).ShouldBe(versions);
        (await db.SourceEntries.CountAsync()).ShouldBe(claims);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        (await db.DatFiles.SingleAsync(d => d.Id == original.Id)).Lifecycle.ShouldBe("Active");
        using var invalid = TestHelpers.CreateMultipartContent("<html>Upstream error</html>", "bad.dat");
        (await client.PostAsync($"/api/dats/{id}/replacement-preview", invalid)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        using var changed = TestHelpers.CreateMultipartContent(Document(second), "changed.dat");
        changed.Add(new StringContent(diff.RootElement.GetProperty("activeSha256").GetString()!), "activeSha256");
        changed.Add(new StringContent(diff.RootElement.GetProperty("candidateSha256").GetString()!), "candidateSha256");
        (await client.PostAsync($"/api/dats/{id}/reviewed-replacement", changed)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        using var approve = TestHelpers.CreateMultipartContent(Document(first + second), "next.dat");
        approve.Add(new StringContent(diff.RootElement.GetProperty("activeSha256").GetString()!), "activeSha256");
        approve.Add(new StringContent(diff.RootElement.GetProperty("candidateSha256").GetString()!), "candidateSha256");
        var apply = await client.PostAsync($"/api/dats/{id}/reviewed-replacement", approve);
        apply.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        await TestHelpers.WaitForJobCompletionAsync(client, job.RootElement.GetProperty("jobId").GetString()!);
        var active = await db.DatFiles.SingleAsync(d => d.DatSourceId == original.DatSourceId && d.Lifecycle == "Active");
        active.Id.ShouldNotBe(original.Id);
        active.GameCount.ShouldBe(2);
        (await db.DatFiles.SingleAsync(d => d.Id == original.Id)).Lifecycle.ShouldBe("Superseded");
        using var stale = TestHelpers.CreateMultipartContent(Document(first + second), "next.dat");
        (await client.PostAsync($"/api/dats/{id}/replacement-preview", stale)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
