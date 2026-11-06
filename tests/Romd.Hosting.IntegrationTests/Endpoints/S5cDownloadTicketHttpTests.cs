using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class S5cDownloadTicketHttpTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    [InlineData("11111111111111111111111111111111")]
    public async Task ExportJob_NoncanonicalOrEmptyGuid_IsAttributed400(string id)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/download-tickets", new { target = "ExportJob", id });
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Export.InvalidDownloadTicket");
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["id"]);
    }
    [Theory]
    [InlineData("original")]
    [InlineData("rvz")]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task ExportJob_AnyNonnullFormat_IsAttributed400(string format)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/download-tickets", new { target = "ExportJob", id = Guid.NewGuid().ToString("D"), format });
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Export.InvalidDownloadTicket");
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["format"]);
    }
    [Fact]
    public async Task ExportJob_OmittedOrNullFormat_CreatesPrivateCanonicalRedeemableTicket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"romd-s5c-ticket-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var job = Completed(path);
            await SeedAsync(ExportJobEntity.FromDomain(job));
            using var client = fixture.CreateAuthenticatedClient();
            using var anonymous = fixture.CreateClient();
            foreach (bool explicitNull in new[] { false, true })
            {
                var body = new Dictionary<string, object?> { ["target"] = "ExportJob", ["id"] = job.Id.ToString("D") };
                if (explicitNull)
                    body["format"] = null;
                using var response = await client.PostAsJsonAsync("/api/download-tickets", body);
                ((int)response.StatusCode).ShouldBe(201);
                response.Headers.CacheControl!.Private.ShouldBeTrue();
                response.Headers.CacheControl.NoStore.ShouldBeTrue();
                response.Headers.Location!.OriginalString.StartsWith("/api/download-tickets/", StringComparison.Ordinal).ShouldBeTrue();
                using var ticket = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                string url = ticket.RootElement.GetProperty("downloadUrl").GetString()!;
                (response.Headers.Location.OriginalString == url).ShouldBeTrue();
                (await anonymous.GetByteArrayAsync(url)).ShouldBe(new byte[] { 1, 2, 3 });
            }
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Manager)]
    public async Task ExportJob_ForeignCreatorOrNonadminManager_PreservesSpecialized403(RomdRoleType role)
    {
        var job = Completed(Path.Combine(Path.GetTempPath(), "not-needed-before-authorization.zip"));
        await SeedAsync(ExportJobEntity.FromDomain(job));
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);
        using var response = await client.PostAsJsonAsync("/api/download-tickets", new { target = "ExportJob", id = job.Id.ToString("D") });
        using var problem = await ProblemEnvelope.ReadAsync(response, 403, "Export.AccessDenied");
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-family")]
    [InlineData("nonterminal")]
    [InlineData("missing-file")]
    public async Task ExportJob_UnavailableArtifact_Is404(string reason)
    {
        Guid id = Guid.NewGuid();
        if (reason == "wrong-family")
        { var upload = UploadJob.Create("not-export.zip", createdAt: DateTimeOffset.UtcNow); upload.Fail("Read-only wrong-family fixture.", failedAt: DateTimeOffset.UtcNow); await SeedAsync(UploadJobEntity.FromDomain(upload)); id = upload.Id; }
        if (reason == "nonterminal")
        { var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow); job.Start("s5c-nonterminal-fixture", startedAt: DateTimeOffset.UtcNow); await SeedAsync(ExportJobEntity.FromDomain(job)); id = job.Id; }
        if (reason == "missing-file")
        { var job = Completed(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip")); await SeedAsync(ExportJobEntity.FromDomain(job)); id = job.Id; }
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/download-tickets", new { target = "ExportJob", id = id.ToString("D") });
        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Export.NotFound");
    }
    private static ExportJob Completed(string path)
    {
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow);
        job.Start("ticket-test", startedAt: DateTimeOffset.UtcNow);
        job.BeginPackaging();
        job.BeginStoring();
        job.SetExportPath(path);
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        return job;
    }
    private async Task SeedAsync(JobEntity row)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        db.Add(row);
        await db.SaveChangesAsync();
    }
}
