using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Hosting;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class DatReplacementReviewEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    [InlineData("short", "bad")]
    public async Task Replacement_InvalidOrMissingPins_IsRejectedBeforeResolvingDat(string? active, string? candidate)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var form = TestHelpers.CreateMultipartContent("<datafile/>", "candidate.dat");
        if (active is not null)
            form.Add(new StringContent(active), "activeSha256");
        if (candidate is not null)
            form.Add(new StringContent(candidate), "candidateSha256");
        using var response = await client.PostAsync($"/api/dats/{IdCoder.Encode(int.MaxValue)}/replacements", form);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        if (active is null || candidate is null)
        {
            // Required multipart binding precedes validation and is attributed to the request body.
            using var missing = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody");
        }
        else
        {
            using var invalid = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
            invalid.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["activeSha256", "candidateSha256"]);
        }
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task Replacement_RequiresManagerBeforeReviewingOrAcceptingInput(bool authenticated, HttpStatusCode expected)
    {
        using var client = fixture.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        using var form = Approval("<datafile/>", new string('a', 64), new string('b', 64));

        using var response = await client.PostAsync($"/api/dats/{IdCoder.Encode(int.MaxValue)}/replacements", form);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_MissingOrEmptyFile_PreservesFileErrorBeforeCatalogLookup(bool empty)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var form = empty ? TestHelpers.CreateMultipartContent("", "empty.dat") : new MultipartFormDataContent();
        form.Add(new StringContent(new string('a', 64)), "activeSha256");
        form.Add(new StringContent(new string('b', 64)), "candidateSha256");

        using var response = await client.PostAsync($"/api/dats/{IdCoder.Encode(int.MaxValue)}/replacements", form);

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "DatReview.FileRequired");
        response.Headers.Location.ShouldBeNull();
        response.Headers.ETag.ShouldBeNull();
    }

    [Fact]
    public async Task Replacement_ValidShapeForMissingCatalog_PreservesNotFoundBeforeDocumentParsing()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var form = Approval("not even XML", new string('a', 64), new string('b', 64));

        using var response = await client.PostAsync($"/api/dats/{IdCoder.Encode(int.MaxValue)}/replacements", form);

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "DatReview.NotFound");
        response.Headers.Location.ShouldBeNull();
        response.Headers.ETag.ShouldBeNull();
    }

    [Theory]
    [InlineData("PUT", "replace")]
    [InlineData("POST", "reviewed-replacement")]
    [InlineData("POST", "replacement-preview")]
    [InlineData("POST", "replacement-changes")]
    [InlineData("POST", "replacement-changes/entry")]
    public async Task RetiredRoutes_AnswerOnlyTheRoutingProblem(string method, string suffix)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            $"/api/dats/{IdCoder.Encode(1)}/{suffix}")
        { Content = JsonContent.Create(new { }) });
        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBeOneOf("Request.RouteNotFound", "Request.MethodNotAllowed");
    }

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
        var preview = await client.PostAsync($"/api/dats/{id}/replacements/preview", candidate);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var diff = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        diff.RootElement.GetProperty("entriesAdded").GetInt32().ShouldBe(1);
        (await db.DatFiles.CountAsync()).ShouldBe(versions);
        (await db.SourceEntries.CountAsync()).ShouldBe(claims);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        (await db.DatFiles.SingleAsync(d => d.Id == original.Id)).Lifecycle.ShouldBe("Active");
        using var invalid = TestHelpers.CreateMultipartContent("<html>Upstream error</html>", "bad.dat");
        (await client.PostAsync($"/api/dats/{id}/replacements/preview", invalid)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        string activePin = diff.RootElement.GetProperty("activeSha256").GetString()!;
        string candidatePin = diff.RootElement.GetProperty("candidateSha256").GetString()!;
        using (var malformed = Approval("<html>Upstream error</html>", activePin, candidatePin))
        using (var rejected = await client.PostAsync($"/api/dats/{id}/replacements", malformed))
        using (await ProblemEnvelope.ReadAsync(rejected, 400, "DatReview.InvalidDocument"))
        { }
        using (var wrongIdentity = Approval(Document(second).Replace(name, "Different catalog", StringComparison.Ordinal), activePin, candidatePin))
        using (var rejected = await client.PostAsync($"/api/dats/{id}/replacements", wrongIdentity))
        using (await ProblemEnvelope.ReadAsync(rejected, 400, "DatReview.Identity"))
        { }
        using (var unchanged = Approval(Document(first), activePin, activePin))
        using (var rejected = await client.PostAsync($"/api/dats/{id}/replacements", unchanged))
        using (await ProblemEnvelope.ReadAsync(rejected, 409, "DatReview.Unchanged"))
        { }
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        using var changed = TestHelpers.CreateMultipartContent(Document(second), "changed.dat");
        changed.Add(new StringContent(diff.RootElement.GetProperty("activeSha256").GetString()!), "activeSha256");
        changed.Add(new StringContent(diff.RootElement.GetProperty("candidateSha256").GetString()!), "candidateSha256");
        using (var rejected = await client.PostAsync($"/api/dats/{id}/replacements", changed))
        using (await ProblemEnvelope.ReadAsync(rejected, 409, "DatReview.Stale"))
        { }
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        using var approve = TestHelpers.CreateMultipartContent(Document(first + second), "next.dat");
        approve.Add(new StringContent(diff.RootElement.GetProperty("activeSha256").GetString()!), "activeSha256");
        approve.Add(new StringContent(diff.RootElement.GetProperty("candidateSha256").GetString()!), "candidateSha256");
        var apply = await client.PostAsync($"/api/dats/{id}/replacements", approve);
        apply.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        job.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["jobId"]);
        apply.Headers.Location?.OriginalString.ShouldBe($"/api/jobs/{job.RootElement.GetProperty("jobId").GetGuid()}");
        apply.Headers.ETag.ShouldBeNull();
        Guid replacementId = job.RootElement.GetProperty("jobId").GetGuid();
        var replacement = await db.Jobs.OfType<ReplaceDatJobEntity>().SingleAsync(row => row.Id == replacementId);
        replacement.ExistingDatId.ShouldBe(original.Id);
        replacement.SourceFilename.ShouldBe("reviewed-catalog.dat");
        replacement.PlatformId.ShouldBeNull();
        replacement.CreatedByUserId.ShouldBeNull();
        await TestHelpers.WaitForJobCompletionAsync(client, job.RootElement.GetProperty("jobId").GetString()!);
        var active = await db.DatFiles.SingleAsync(d => d.DatSourceId == original.DatSourceId && d.Lifecycle == "Active");
        active.Id.ShouldNotBe(original.Id);
        active.GameCount.ShouldBe(2);
        (await db.DatFiles.SingleAsync(d => d.Id == original.Id)).Lifecycle.ShouldBe("Superseded");
        using var stale = TestHelpers.CreateMultipartContent(Document(first + second), "next.dat");
        (await client.PostAsync($"/api/dats/{id}/replacements/preview", stale)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var staleApproval = Approval("not XML", activePin, candidatePin);
        using var staleResponse = await client.PostAsync($"/api/dats/{id}/replacements", staleApproval);
        using var staleProblem = await ProblemEnvelope.ReadAsync(staleResponse, 409, "DatReview.Stale");
    }

    private static MultipartFormDataContent Approval(string document, string active, string candidate)
    {
        var form = TestHelpers.CreateMultipartContent(document, "caller-name.dat");
        form.Add(new StringContent(active), "activeSha256");
        form.Add(new StringContent(candidate), "candidateSha256");
        return form;
    }
}
