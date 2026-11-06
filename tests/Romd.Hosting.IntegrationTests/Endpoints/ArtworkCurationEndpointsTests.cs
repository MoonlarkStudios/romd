using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ArtworkCurationEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Completion_ValidatesScopesAndReturnsServerRunHistory()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var invalid = await client.PostAsJsonAsync("/api/artwork/completion/preview", new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Invalid"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var preview = await client.PostAsJsonAsync("/api/artwork/completion/preview", new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "AllTracked"));
        preview.StatusCode.ShouldBe(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await preview.Content.ReadFromJsonAsync<ArtworkCompletionPreviewDto>())!.Roles.Count.ShouldBe(4);
        using var history = await client.GetAsync("/api/jobs?jobType=artwork-completion");
        history.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Completion_ExactReplayKeepsJobReferenceBodyLocationAndCreator_AndChangedIntentIsConflict()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string titleId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var name = $"Completion replay {Guid.NewGuid():N}";
            var title = new TitleEntity
            {
                PlatformId = await db.Platforms.OrderBy(row => row.Id).Select(row => row.Id).FirstAsync(),
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            titleId = IdCoder.Encode(title.Id);
        }
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), [titleId], Mode: "Enrich");
        using var accepted = await client.PostAsJsonAsync("/api/artwork/completion", request);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(field => field.Name).ShouldBe(["jobId"]);
        body.RootElement.GetProperty("jobId").GetGuid().ShouldBe(request.RequestId);
        accepted.Headers.Location!.OriginalString.ShouldBe($"/api/jobs/{request.RequestId}");
        accepted.Headers.ETag.ShouldBeNull();
        Guid? creator;
        using (var scope = fixture.Services.CreateScope())
            creator = await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Jobs
                .Where(row => row.Id == request.RequestId).Select(row => row.CreatedByUserId).SingleAsync();

        using var replay = await client.PostAsJsonAsync("/api/artwork/completion", request with { TitleIds = [titleId, titleId] });
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted, await replay.Content.ReadAsStringAsync());
        (await replay.Content.ReadFromJsonAsync<JobReference>())!.JobId.ShouldBe(request.RequestId);
        replay.Headers.Location!.OriginalString.ShouldBe(accepted.Headers.Location.OriginalString);
        using var conflict = await client.PostAsJsonAsync("/api/artwork/completion", request with { Mode = "Artwork" });
        using (await ProblemEnvelope.ReadAsync(conflict, 409, "Artwork.RequestConflict"))
        { }
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var job = await db.Jobs.SingleAsync(row => row.Id == request.RequestId);
            job.CreatedByUserId.ShouldBe(creator);
            (await db.JobDispatches.CountAsync(row => row.JobId == request.RequestId)).ShouldBe(1);
        }
    }

    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Contributor)]
    public async Task Completion_BelowManager_CannotAcceptWork(RomdRoleType role)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);

        using var response = await client.PostAsJsonAsync("/api/artwork/completion", new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "AllTracked"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CompletionPreview_EnrichmentFilters_UseCatalogVocabulary()
    {
        using var client = fixture.CreateAuthenticatedClient();
        ArtworkCompletionFilterDto[] invalid = [new(Enrichment: []), new(Enrichment: [""]), new(Enrichment: ["Bogus"]),
            new(Enrichment: ["Settled", "Processing"]), new(EnrichmentStatus: "NeedsReview")];
        foreach (var scope in new[] { "Filtered", "Automatic" })
        {
            foreach (var filters in invalid)
            {
                using var rejected = await client.PostAsJsonAsync("/api/artwork/completion/preview",
                    new ArtworkCompletionRequest(Guid.NewGuid(), Scope: scope, Filters: filters, Mode: "Enrich"));
                rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{scope} {filters}");
            }
            using var accepted = await client.PostAsJsonAsync("/api/artwork/completion/preview", new ArtworkCompletionRequest(Guid.NewGuid(),
                Scope: scope, Filters: new(EnrichmentStatus: "Failed", Enrichment: ["NeedsReview", "Blocked"]), Mode: "Enrich"));
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task CompletionRun_ExecutedEnrichRun_CountsEqualListsAndRetiredStatusesAreRejected()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string titleId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var name = $"Run feedback {Guid.NewGuid():N}";
            var title = new TitleEntity
            {
                PlatformId = await db.Platforms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(),
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            titleId = IdCoder.Encode(title.Id);
        }

        using var queued = await client.PostAsJsonAsync("/api/artwork/completion", new ArtworkCompletionRequest(Guid.NewGuid(), [titleId], Mode: "Enrich"));
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync());
        using var accepted = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
        var fields = accepted.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        fields.ShouldBe(["jobId"]);
        queued.Headers.Location?.OriginalString.ShouldBe($"/api/jobs/{accepted.RootElement.GetProperty("jobId").GetGuid()}");
        queued.Headers.ETag.ShouldBeNull();
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;
        await TestHelpers.WaitForJobCompletionAsync(client, jobId);

        using var statusResponse = await client.GetAsync($"/api/jobs/{jobId}");
        statusResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var statusBody = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        var readFields = statusBody.RootElement.EnumerateObject().Select(field => field.Name).ToArray();
        readFields.ShouldNotContain("review");
        readFields.ShouldNotContain("failed");
        readFields.ShouldNotContain("unfinished");
        readFields.ShouldNotContain("working");
        readFields.ShouldContain("nextAttemptAt");
        readFields.ShouldNotContain("ready");
        readFields.ShouldNotContain("needsAttention");
        var job = (await statusResponse.Content.ReadFromJsonAsync<ArtworkCompletionOperationDto>(RunJson))!;
        (job.Total, job.Processed, job.NextAttemptAt).ShouldBe((1, 1, (DateTimeOffset?)null));
        var summary = (await client.GetFromJsonAsync<ArtworkCompletionSummaryDto>($"/api/jobs/{jobId}/artwork-summary", RunJson))!;
        foreach (var (status, count) in new[] { ("Review", summary.Review), ("Failed", summary.Failed), ("Unfinished", summary.Unfinished), ("Working", summary.Working) })
            (await client.GetFromJsonAsync<Page<ArtworkCompletionItemDto>>($"/api/jobs/{jobId}/artwork-items?status={status}", RunJson))!.Items.Count.ShouldBe(count, status);
        var item = (await client.GetFromJsonAsync<Page<ArtworkCompletionItemDto>>($"/api/jobs/{jobId}/artwork-items", RunJson))!.Items.ShouldHaveSingleItem();
        (item.TitleId, item.Completion).ShouldBe((titleId, ArtworkCompletionState.Done));
        // The item ran after schema 44, so its metadata execution is recorded rather than unknown.
        item.MetadataOutcome.ShouldNotBeNull();
        item.Now.ShouldNotBeNull();

        foreach (var retired in new[] { "Unresolved", "NeedsMatch", "NeedsAttention", "1", "review" })
            (await client.GetAsync($"/api/jobs/{jobId}/artwork-items?status={retired}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest, retired);
        foreach (var retired in new[] { null, "Unresolved", "Review", "Working" })
        {
            using var rejected = await client.PostAsJsonAsync("/api/artwork/completion",
                new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: Guid.Parse(jobId), RetryStatus: retired));
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest, retired ?? "(none)");
        }
        // No metadata provider is configured here, so the metadata step fails and nothing is left unfinished.
        (summary.Failed, summary.Unfinished).ShouldBe((1, 0));
        using var empty = await client.PostAsJsonAsync("/api/artwork/completion",
            new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: Guid.Parse(jobId), RetryStatus: "Unfinished"));
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await empty.Content.ReadAsStringAsync());
        using var retry = await client.PostAsJsonAsync("/api/artwork/completion",
            new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: Guid.Parse(jobId), RetryStatus: "Failed"));
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retry.Content.ReadAsStringAsync());
        var reference = (await retry.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.JobReference>())!;
        var retried = (await client.GetFromJsonAsync<ArtworkCompletionOperationDto>($"/api/jobs/{reference.JobId}", RunJson))!;
        (retried.Total, retried.Mode).ShouldBe((1, "Enrich"));
    }

    private static readonly JsonSerializerOptions RunJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Settings = "/api/artwork-providers/steamgriddb/settings";

    [Fact]
    public async Task Providers_RegistryIncludesBothAdaptersAndTitleScopeValidatesInput()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/artwork-providers");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var providers = (await response.Content.ReadFromJsonAsync<ArtworkProviderDto[]>())!;
        providers!.Select(provider => provider.Id).ShouldContain("igdb");
        providers.Select(provider => provider.Id).ShouldContain("steamgriddb");
        using var invalid = await client.GetAsync($"/api/artwork-providers?titleId={IdCoder.Encode(1)}&role=invalid");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missing = await client.GetAsync($"/api/artwork-providers?titleId={IdCoder.Encode(int.MaxValue)}&role=Hero");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("GET", Settings)]
    [InlineData("PUT", Settings)]
    [InlineData("POST", Settings + "/test-connection")]
    [InlineData("GET", "/api/artwork-providers")]
    [InlineData("POST", "/api/artwork/completion")]
    [InlineData("GET", "/api/jobs/11111111-1111-1111-1111-111111111111/artwork-summary")]
    [InlineData("GET", "/api/titles/test/artwork")]
    [InlineData("PUT", "/api/titles/test/artwork/Poster")]
    [InlineData("GET", "/api/titles/test/artwork-assets")]
    [InlineData("POST", "/api/titles/test/artwork-candidates/test/import")]
    [InlineData("DELETE", "/api/titles/test/artwork-assets/test")]
    [InlineData("PUT", "/api/titles/test/hidden-artwork-candidates/test")]
    public async Task ArtworkEndpoints_Anonymous_RequireAuthentication(string method, string path)
    {
        using var client = fixture.CreateClient();
        using var request = CreateRequest(method, path);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", Settings, RomdRoleType.User)]
    [InlineData("PUT", Settings, RomdRoleType.User)]
    [InlineData("POST", Settings + "/test-connection", RomdRoleType.User)]
    [InlineData("GET", Settings, RomdRoleType.Manager)]
    [InlineData("PUT", Settings, RomdRoleType.Manager)]
    [InlineData("POST", Settings + "/test-connection", RomdRoleType.Manager)]
    public async Task Settings_NonAdmin_CannotReadOrChangeCredentials(string method, string path, RomdRoleType role)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);
        using var request = CreateRequest(method, path);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    [InlineData("import")]
    [InlineData("hide")]
    public async Task CandidateEndpoints_ArbitraryReference_IsRejectedWithoutPersistentChanges(string operation)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Arbitrary reference {Guid.NewGuid():N}";
        var seeded = new TitleEntity
        {
            PlatformId = await db.Platforms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(seeded);
        await db.SaveChangesAsync();
        var before = await Counts(db);
        var title = $"/api/titles/{IdCoder.Encode(seeded.Id)}";
        const string reference = "http://127.0.0.1/private";
        using var request = operation switch
        {
            "preview" => Request(HttpMethod.Post, $"{title}/artwork-candidates/preview", null, new PreviewArtworkRequest(reference)),
            "apply" => Request(HttpMethod.Post, $"{title}/artwork/Poster/apply", "\"0\"",
                new ApplyArtworkRequest(Guid.NewGuid(), reference)),
            // The import's title precondition is current, so only the reference can fail.
            "import" => Request(HttpMethod.Post, $"{title}/artwork-candidates/{ArtworkCandidateIds.Of(reference)}/import",
                $"\"{seeded.Revision}\"", new ImportGalleryArtworkRequest(reference)),
            _ => Request(HttpMethod.Put, $"{title}/hidden-artwork-candidates/{ArtworkCandidateIds.Of(reference)}",
                $"\"{Guid.NewGuid()}\"", new HideArtworkCandidateRequest(reference))
        };
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Counts(db)).ShouldBe(before);
    }

    [Fact]
    public async Task Settings_Admin_StoresWriteOnlySecretAndChecksRevision()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var initial = (await client.GetFromJsonAsync<SteamGridDbSettingsDto>(Settings))!;
        const string key = "controlled-steamgriddb-secret";
        try
        {
            using var saveRequest = Request(HttpMethod.Put, Settings, $"\"{initial.Revision}\"",
                new UpdateSteamGridDbSettingsRequest(false, key));
            using var saved = await client.SendAsync(saveRequest);
            saved.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await saved.Content.ReadAsStringAsync()).ShouldNotContain(key);
            var state = (await saved.Content.ReadFromJsonAsync<SteamGridDbSettingsDto>())!;
            state.HasApiKey.ShouldBeTrue();
            state.Revision.ShouldNotBe(initial.Revision);
            using var staleRequest = Request(HttpMethod.Put, Settings, $"\"{initial.Revision}\"",
                new UpdateSteamGridDbSettingsRequest(false, null, true));
            using var stale = await client.SendAsync(staleRequest);
            stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        }
        finally
        {
            var current = (await client.GetFromJsonAsync<SteamGridDbSettingsDto>(Settings))!;
            using var clearRequest = Request(HttpMethod.Put, Settings, $"\"{current.Revision}\"",
                new UpdateSteamGridDbSettingsRequest(false, null, true));
            using var cleared = await client.SendAsync(clearRequest);
            cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static HttpRequestMessage CreateRequest(string method, string path) => new(new HttpMethod(method), path)
    {
        // Match the JSON endpoint contract so routing selects the protected endpoint.
        Content = path == Settings && method == "PUT"
            ? JsonContent.Create(new UpdateSteamGridDbSettingsRequest(false, null))
            : path.EndsWith("/artwork/Poster", StringComparison.Ordinal)
                ? JsonContent.Create(new { mode = "Automatic" })
            : path.EndsWith("/import", StringComparison.Ordinal)
                ? JsonContent.Create(new ImportGalleryArtworkRequest("reference"))
            : path.Contains("/hidden-artwork-candidates/", StringComparison.Ordinal) && method == "PUT"
                ? JsonContent.Create(new HideArtworkCandidateRequest("reference"))
                : null
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, string? ifMatch, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    private static async Task<(int Titles, int Assets, int Selections, int Files, int Jobs)> Counts(RomdDbContext db) =>
        (await db.Titles.CountAsync(), await db.ArtworkAssets.CountAsync(), await db.ArtworkSelections.CountAsync(),
            await db.Files.CountAsync(), await db.Jobs.CountAsync());
}
