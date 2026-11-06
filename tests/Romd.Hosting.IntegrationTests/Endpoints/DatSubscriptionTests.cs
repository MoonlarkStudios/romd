using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Source.Dat;
using Romd.Host.Authorization;
using Romd.Hosting.IntegrationTests.Hosting;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class DatSubscriptionTests(IntegrationTestFixture fixture)
{
    private sealed class CandidateClient : ISignedDatCatalogClient
    {
        public bool Enabled => true;
        public ErrorOr<byte[]> Result { get; set; }
        public IReadOnlyList<PublishedDatCatalog> Catalogs { get; set; } = [];
        public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<IReadOnlyList<PublishedDatCatalog>>>(Catalogs.ToArray());
        public Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct) => Task.FromResult(Result);
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Romd.Admin.Host.Program> App, HttpClient Client) Host(CandidateClient fake)
    {
        var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ISignedDatCatalogClient>(fake)));
        var client = app.CreateClient();
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return (app, client);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A subscription changes read: the list, or one entry when <paramref name="entryId" /> is given.</summary>
    private static string Changes(string subscriptionId, string? entryId, params (string Name, object Value)[] query) =>
        $"/api/dat-subscriptions/{subscriptionId}/changes{(entryId is null ? "" : "/" + entryId)}?" +
        string.Join('&', query.Select(item => $"{item.Name}={Uri.EscapeDataString(item.Value.ToString()!)}"));

    private static string[] ErrorFields(JsonElement problem) =>
        problem.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("psx", "redump")]
    [InlineData("snes", "no-intro")]
    public async Task Enrollment_FirstReviewCreatesNoClaims_ApprovedJobsImportAndReplace_ThenFailureRetainsActive(string systemId, string provider)
    {
        string catalogId = $"{provider}/{systemId}/enrollment-test";
        string name = $"ROMD Enrollment Test - {systemId}";
        int initialCount = systemId == "snes" ? 2 : 1;
        string undumped = systemId == "snes" ? "<game name='Enrollment Undumped'><rom name='unknown.sfc' status='nodump'/></game>" : "";
        byte[] Document(bool update) => Encoding.UTF8.GetBytes($"<datafile><header><name>{name}</name></header><game name='Enrollment One'><rom name='one.bin' size='1' crc='12345678'/></game>{undumped}{(update ? "<game name='Enrollment Two'><rom name='two.bin' size='2' crc='87654321'/></game>" : "")}</datafile>");
        var fake = new CandidateClient { Result = Document(false), Catalogs = [new(catalogId, systemId, name, provider, "healthy", new string('a', 64), initialCount, initialCount, null)] };
        var (app, client) = Host(fake);
        using var appScope = app;
        using var clientScope = client;
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync("/api/dat-catalogs")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/dat-subscriptions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var directory = await client.GetFromJsonAsync<Models.DatCatalogDirectoryDto>("/api/dat-catalogs");
        directory!.Catalogs.Single().CatalogId.ShouldBe(catalogId);
        var created = await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var subscription = (await created.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>())!;
        created.Headers.Location!.OriginalString.ShouldBe($"/api/dat-subscriptions/{subscription.Id}");
        subscription.State.ShouldBe("ReadyToImport");
        subscription.ActiveDatId.ShouldBeNull();
        (await client.GetFromJsonAsync<Models.DatCatalogSubscriptionDto[]>("/api/dat-subscriptions"))!
            .ShouldContain(row => row.Id == subscription.Id && row.CatalogId == catalogId);
        // Creation is idempotent on the catalog: a replay answers 200 with the existing subscription and no check.
        var stored = (await client.GetFromJsonAsync<Models.DatCatalogSubscriptionDto>($"/api/dat-subscriptions/{subscription.Id}"))!;
        var replay = await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId });
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>()).ShouldBe(stored);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.DatFiles.CountAsync(d => d.Name == name)).ShouldBe(0);
        var jobs = await db.Jobs.CountAsync();
        async Task<Models.DatCatalogSubscriptionDto> Check()
        {
            var result = await client.PostAsync($"/api/dat-subscriptions/{subscription.Id}/check", null);
            result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>())!;
        }
        async Task<Models.DatReplacementPreview> Preview()
        {
            var result = await client.GetAsync($"/api/dat-subscriptions/{subscription.Id}/preview");
            result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<Models.DatReplacementPreview>())!;
        }
        async Task<string> Apply(Models.DatReplacementPreview preview)
        {
            var result = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply", new { preview.ActiveSha256, preview.CandidateSha256 });
            result.StatusCode.ShouldBe(HttpStatusCode.Accepted, await result.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("jobId").GetString()!;
        }
        var first = await Preview();
        first.ActiveSha256.ShouldBeEmpty();
        first.EntriesAdded.ShouldBe(initialCount);
        first.ActiveFiles.ShouldBe(0);
        first.CandidateFiles.ShouldBe(initialCount);
        // An installed-nothing first import pins an explicitly empty activeSha256 query value.
        var changesResponse = await client.GetAsync(Changes(subscription.Id, null,
            ("activeSha256", first.ActiveSha256), ("candidateSha256", first.CandidateSha256), ("limit", 1)));
        changesResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await changesResponse.Content.ReadAsStringAsync());
        var changes = (await changesResponse.Content.ReadFromJsonAsync<CommonModels.Page<Models.DatEntryChange>>())!;
        changes.Items.Single().Name.ShouldBe("Enrollment One");
        changes.HasNextPage.ShouldBe(initialCount > 1);
        if (changes.NextCursor is { } cursor)
        {
            var nextResponse = await client.GetAsync(Changes(subscription.Id, null,
                ("activeSha256", first.ActiveSha256), ("candidateSha256", first.CandidateSha256), ("cursor", cursor), ("limit", 1)));
            var next = (await nextResponse.Content.ReadFromJsonAsync<CommonModels.Page<Models.DatEntryChange>>())!;
            next.Items.Single().Name.ShouldBe("Enrollment Undumped");
            next.HasNextPage.ShouldBeFalse();
        }
        var stale = await client.GetAsync(Changes(subscription.Id, null,
            ("activeSha256", first.ActiveSha256), ("candidateSha256", new string('0', 64))));
        (await ProblemAsync(stale, HttpStatusCode.Conflict)).GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Stale");
        var detailsResponse = await client.GetAsync(Changes(subscription.Id, changes.Items[0].Id,
            ("activeSha256", first.ActiveSha256), ("candidateSha256", first.CandidateSha256)));
        detailsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await detailsResponse.Content.ReadAsStringAsync());
        var details = (await detailsResponse.Content.ReadFromJsonAsync<Models.DatEntryChangeDetail>())!;
        details.Name.ShouldBe("Enrollment One");
        details.Change.ShouldBe("Added");
        details.Files.Items.Single().Fields.Single(f => f.Field == "Size").After.ShouldBe("1");
        var staleDetail = await client.GetAsync(Changes(subscription.Id, changes.Items[0].Id,
            ("activeSha256", first.ActiveSha256), ("candidateSha256", new string('0', 64))));
        (await ProblemAsync(staleDetail, HttpStatusCode.Conflict)).GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Stale");
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        var initialJob = await Apply(first);
        (await Apply(first)).ShouldBe(initialJob);
        await TestHelpers.WaitForJobCompletionAsync(client, initialJob);
        var status = await client.GetFromJsonAsync<Models.DatCatalogSubscriptionDto>($"/api/dat-subscriptions/{subscription.Id}");
        status!.State.ShouldBe("UpToDate");
        status.ActiveDatId.ShouldNotBeNull();
        var initial = await db.DatFiles.SingleAsync(d => d.Name == name && d.Lifecycle == "Active");
        initial.GameCount.ShouldBe(initialCount);
        (await client.GetByteArrayAsync($"/api/dats/{status.ActiveDatId}/download")).ShouldBe(Document(false));
        if (systemId == "snes")
            (await db.DatRoms.Where(r => db.DatGames.Any(g => g.Id == r.DatGameId && g.DatFileId == initial.Id) && r.Status == "nodump").CountAsync()).ShouldBe(1);
        var jobsBeforeUnchanged = await db.Jobs.CountAsync();
        (await Check()).State.ShouldBe("UpToDate");
        (await db.Jobs.CountAsync()).ShouldBe(jobsBeforeUnchanged);
        // With no candidate waiting for review, a preview is a conflict with current state.
        (await ProblemAsync(await client.GetAsync($"/api/dat-subscriptions/{subscription.Id}/preview"), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Stale");
        fake.Result = Document(true);
        (await Check()).State.ShouldBe("UpdateAvailable");
        var update = await Preview();
        update.EntriesAdded.ShouldBe(1);
        update.EntriesRemoved.ShouldBe(0);
        var staleApply = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply", new { first.ActiveSha256, first.CandidateSha256 });
        (await ProblemAsync(staleApply, HttpStatusCode.Conflict)).GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Stale");
        var updateJob = await Apply(update);
        await TestHelpers.WaitForJobCompletionAsync(client, updateJob);
        var active = await db.DatFiles.SingleAsync(d => d.DatSourceId == initial.DatSourceId && d.Lifecycle == "Active");
        active.GameCount.ShouldBe(initialCount + 1);
        fake.Result = Error.Failure("Test.Unavailable", "Publisher unavailable");
        (await Check()).State.ShouldBe("CheckFailed");
        (await db.DatFiles.SingleAsync(d => d.Id == active.Id)).Lifecycle.ShouldBe("Active");
        var acceptedJobs = await db.Jobs.CountAsync();
        // Repeating an already accepted approval returns its durable receipt, never a new job.
        (await Apply(update)).ShouldBe(updateJob);
        (await db.Jobs.CountAsync()).ShouldBe(acceptedJobs);
        (await client.DeleteAsync($"/api/dat-subscriptions/{subscription.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync($"/api/dat-subscriptions/{subscription.Id}/check", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_RejectsInvalidInputBeforeState_AndStateConflictsAre409()
    {
        const string catalogId = "redump/psx/absent-catalog-test";
        var fake = new CandidateClient { Result = Error.Failure("Test.Unused", "Unused") };
        var (app, client) = Host(fake);
        using var appScope = app;
        using var clientScope = client;
        var invalid = await ProblemAsync(await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId = "", datId = "not-a-sqid" }),
            HttpStatusCode.BadRequest);
        invalid.GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.InvalidRequest");
        ErrorFields(invalid).ShouldBe(["catalogId", "datId"]);
        ErrorFields(await ProblemAsync(await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId = new string('c', 101) }),
            HttpStatusCode.BadRequest)).ShouldBe(["catalogId"]);
        // A catalog the publisher does not list depends on server state, so it is a conflict, not a 400.
        (await ProblemAsync(await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId }), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Unavailable");
        fake.Catalogs = [new(catalogId, "no-such-system", "Absent", "redump", "healthy", new string('a', 64), 1, 1, null)];
        (await ProblemAsync(await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId }), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.PlatformMissing");
    }

    [Fact]
    public async Task Preview_ServerHeldDocumentFailureAndMissingPlatform_AreConflicts()
    {
        const string catalogId = "redump/psx/server-held-review-test";
        const string name = "ROMD Server Held Review Test";
        var fake = new CandidateClient
        {
            Result = Encoding.UTF8.GetBytes($"<datafile><header><name>{name}</name></header><game name='One'><rom name='one.bin' size='1' crc='12345678'/></game></datafile>"),
            Catalogs = [new(catalogId, "psx", name, "redump", "healthy", new string('a', 64), 1, 1, null)]
        };
        var (app, client) = Host(fake);
        using var appScope = app;
        using var clientScope = client;
        var created = await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var subscription = (await created.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>())!;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        // A stale server-held identity is a state conflict even though the same document error in an upload is a 400.
        await db.DatSubscriptions.Where(row => row.CatalogId == catalogId).ExecuteUpdateAsync(update => update.SetProperty(row => row.ExpectedName, "Another Catalog"));
        (await ProblemAsync(await client.GetAsync($"/api/dat-subscriptions/{subscription.Id}/preview"), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatReview.Identity");
        await db.DatSubscriptions.Where(row => row.CatalogId == catalogId).ExecuteUpdateAsync(update => update.SetProperty(row => row.PlatformId, (int?)null));
        (await ProblemAsync(await client.GetAsync($"/api/dat-subscriptions/{subscription.Id}/preview"), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.PlatformMissing");
    }

    [Fact]
    public async Task Changes_ValidatesEveryInputBeforeResolvingTheSubscription()
    {
        var (app, client) = Host(new CandidateClient());
        using var appScope = app;
        using var clientScope = client;
        string unknown = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();
        string hash = new('a', 64);
        // The declared-parameter stage reports every constraint failure before the subscription resolves.
        var invalid = await ProblemAsync(await client.GetAsync(Changes(unknown, null, ("activeSha256", "short"), ("candidateSha256", hash),
            ("change", "Renamed"), ("query", new string('q', 201)), ("cursor", "bad cursor"), ("limit", 51))), HttpStatusCode.BadRequest);
        invalid.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
        ErrorFields(invalid).ShouldBe(["activeSha256", "change", "limit", "query"]);
        var emptyChange = await ProblemAsync(await client.GetAsync(Changes(unknown, null,
            ("activeSha256", ""), ("candidateSha256", hash), ("change", ""))), HttpStatusCode.BadRequest);
        ErrorFields(emptyChange).ShouldBe(["change"]);
        var missing = await ProblemAsync(await client.GetAsync(Changes(unknown, null, ("candidateSha256", hash))), HttpStatusCode.BadRequest);
        ErrorFields(missing).ShouldBe(["activeSha256"]);
        var entryPins = await ProblemAsync(await client.GetAsync(Changes(unknown, DatEntryId.Encode("Game"),
            ("activeSha256", ""), ("candidateSha256", ""), ("limit", 0))), HttpStatusCode.BadRequest);
        ErrorFields(entryPins).ShouldBe(["candidateSha256", "limit"]);
        // The operation's own validation follows: the cursor scope and the entry id.
        var cursor = await ProblemAsync(await client.GetAsync(Changes(unknown, null,
            ("activeSha256", ""), ("candidateSha256", hash), ("cursor", "bad cursor"))), HttpStatusCode.BadRequest);
        cursor.GetProperty("errorCode").GetString().ShouldBe("DatReview.InvalidRequest");
        ErrorFields(cursor).ShouldBe(["cursor"]);
        var entry = await ProblemAsync(await client.GetAsync(Changes(unknown, "R2FtZQ==",
            ("activeSha256", ""), ("candidateSha256", hash), ("cursor", "bad cursor"))), HttpStatusCode.BadRequest);
        entry.GetProperty("errorCode").GetString().ShouldBe("DatReview.InvalidRequest");
        ErrorFields(entry).ShouldBe(["cursor", "entryId"]);
        (await ProblemAsync(await client.GetAsync(Changes(unknown, null, ("activeSha256", ""), ("candidateSha256", hash))),
            HttpStatusCode.NotFound)).GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.NotFound");
        (await ProblemAsync(await client.GetAsync(Changes(unknown, DatEntryId.Encode("Game"), ("activeSha256", ""), ("candidateSha256", hash))),
            HttpStatusCode.NotFound)).GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.NotFound");
        (await ProblemAsync(await client.GetAsync($"/api/dat-subscriptions/{unknown}/preview"), HttpStatusCode.NotFound))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.NotFound");
        var apply = await ProblemAsync(await client.PostAsJsonAsync($"/api/dat-subscriptions/{unknown}/apply",
            new { activeSha256 = "short", candidateSha256 = "" }), HttpStatusCode.BadRequest);
        ErrorFields(apply).ShouldBe(["activeSha256", "candidateSha256"]);
    }

    [Fact]
    public async Task ReplacementChanges_PagesTheUploadedCandidateByCursor_AndRejectsStaleOrForeignPages()
    {
        const string name = "ROMD Replacement Paging Test";
        static string Document(params string[] entries) => $"<datafile><header><name>{name}</name></header>" +
            string.Concat(entries.Select(entry => $"<game name='{entry}'><rom name='{entry}.bin' size='1' crc='11111111'/></game>")) + "</datafile>";
        using var client = fixture.CreateAuthenticatedClient();
        using var upload = TestHelpers.CreateMultipartContent(Document("Paging Alpha"), "paging.dat");
        var uploaded = await client.PostAsync("/api/upload/dat", upload);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using (var accepted = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()))
            await TestHelpers.WaitForJobCompletionAsync(client, accepted.RootElement.GetProperty("jobId").GetString()!);
        string datId = await TestHelpers.GetDatIdByNameAsync(client, name);
        string candidate = Document("Paging Alpha", "Paging Beta", "Paging Gamma");
        using var previewForm = TestHelpers.CreateMultipartContent(candidate, "review.dat");
        var previewResponse = await client.PostAsync($"/api/dats/{datId}/replacements/preview", previewForm);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await previewResponse.Content.ReadAsStringAsync());
        var preview = (await previewResponse.Content.ReadFromJsonAsync<Models.DatReplacementPreview>())!;
        preview.EntriesAdded.ShouldBe(2);
        MultipartFormDataContent Form(string? cursor = null, string? candidateSha256 = null, string? query = null, string limit = "1")
        {
            var form = TestHelpers.CreateMultipartContent(candidate, "review.dat");
            form.Add(new StringContent(preview.ActiveSha256), "activeSha256");
            form.Add(new StringContent(candidateSha256 ?? preview.CandidateSha256), "candidateSha256");
            form.Add(new StringContent(limit), "limit");
            if (cursor is not null)
                form.Add(new StringContent(cursor), "cursor");
            if (query is not null)
                form.Add(new StringContent(query), "query");
            return form;
        }
        using var firstForm = Form();
        var firstResponse = await client.PostAsync($"/api/dats/{datId}/replacements/changes", firstForm);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await firstResponse.Content.ReadAsStringAsync());
        var first = (await firstResponse.Content.ReadFromJsonAsync<CommonModels.Page<Models.DatEntryChange>>())!;
        first.Items.Single().Name.ShouldBe("Paging Beta");
        first.HasNextPage.ShouldBeTrue();
        using var secondForm = Form(first.NextCursor);
        var second = (await (await client.PostAsync($"/api/dats/{datId}/replacements/changes", secondForm)).Content
            .ReadFromJsonAsync<CommonModels.Page<Models.DatEntryChange>>())!;
        second.Items.Single().Name.ShouldBe("Paging Gamma");
        second.HasNextPage.ShouldBeFalse();
        second.NextCursor.ShouldBeNull();
        // A cursor is bound to its filters; one from another filter set is invalid input.
        using var foreignForm = Form(first.NextCursor, query: "Gamma");
        ErrorFields(await ProblemAsync(await client.PostAsync($"/api/dats/{datId}/replacements/changes", foreignForm), HttpStatusCode.BadRequest))
            .ShouldBe(["cursor"]);
        using var staleForm = Form(candidateSha256: new string('0', 64));
        (await ProblemAsync(await client.PostAsync($"/api/dats/{datId}/replacements/changes", staleForm), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatReview.Stale");
        using var detailForm = Form();
        var detailResponse = await client.PostAsync($"/api/dats/{datId}/replacements/changes/{first.Items[0].Id}", detailForm);
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await detailResponse.Content.ReadAsStringAsync());
        var detail = (await detailResponse.Content.ReadFromJsonAsync<Models.DatEntryChangeDetail>())!;
        detail.Name.ShouldBe("Paging Beta");
        detail.Files.Items.Single().Name.ShouldBe("Paging Beta.bin");
        using var unchangedForm = Form();
        (await ProblemAsync(await client.PostAsync($"/api/dats/{datId}/replacements/changes/{DatEntryId.Encode("Paging Alpha")}", unchangedForm),
            HttpStatusCode.NotFound)).GetProperty("errorCode").GetString().ShouldBe("DatReview.Entry");
        using var staleDetailForm = Form(candidateSha256: new string('0', 64));
        (await ProblemAsync(await client.PostAsync($"/api/dats/{datId}/replacements/changes/{first.Items[0].Id}", staleDetailForm),
            HttpStatusCode.Conflict)).GetProperty("errorCode").GetString().ShouldBe("DatReview.Stale");
        // An out-of-range form limit is a declared-parameter failure, the same stage as a query limit.
        foreach (string route in new[] { "replacements/changes", $"replacements/changes/{first.Items[0].Id}" })
        {
            using var limitForm = Form(limit: "51");
            var limit = await ProblemAsync(await client.PostAsync($"/api/dats/{datId}/{route}", limitForm), HttpStatusCode.BadRequest);
            limit.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
            ErrorFields(limit).ShouldBe(["limit"]);
        }
    }

    [Fact]
    public async Task Create_WithAnInstalledSource_BindsThatSourceAndReplaysIt()
    {
        const string catalogId = "redump/psx/explicit-source-test";
        const string name = "ROMD Explicit Source Test";
        byte[] document = Encoding.UTF8.GetBytes($"<datafile><header><name>{name}</name></header><game name='Explicit One'><rom name='one.bin' size='1' crc='12345678'/></game></datafile>");
        var fake = new CandidateClient { Result = document, Catalogs = [new(catalogId, "psx", name, "redump", "healthy", new string('a', 64), 1, 1, null)] };
        var (app, client) = Host(fake);
        using var appScope = app;
        using var clientScope = client;
        await TestHelpers.UploadDatWithPlatformAsync(client, Encoding.UTF8.GetString(document), "explicit-source.dat", "psx");
        string datId = await TestHelpers.GetDatIdByNameAsync(client, name);
        var created = await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId, datId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var subscription = (await created.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>())!;
        subscription.ActiveDatId.ShouldBe(datId);
        subscription.State.ShouldBe("UpToDate");
        var replay = await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId, datId });
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<Models.DatCatalogSubscriptionDto>())!.Id.ShouldBe(subscription.Id);
        string unknown = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();
        (await ProblemAsync(await client.PostAsJsonAsync("/api/dat-subscriptions", new { catalogId, datId = unknown }), HttpStatusCode.Conflict))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Unavailable");
        (await client.DeleteAsync($"/api/dat-subscriptions/{subscription.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Stop_MapsBusyAndNotFound_WithSharedScopedStore()
    {
        var (app, client) = Host(new CandidateClient());
        using var appScope = app;
        using var clientScope = client;
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IDatCatalogEnrollmentService>()
            .ShouldBeSameAs(services.GetRequiredService<IDatSubscriptionStopStore>());
        var db = services.GetRequiredService<RomdDbContext>();
        string catalog = $"test/stop/{Guid.NewGuid():N}";
        var row = new DatSubscriptionEntity { CatalogId = catalog };
        db.DatSubscriptions.Add(row);
        await db.SaveChangesAsync();
        string identity = new Romd.Application.Common.Ids.Sqid(row.Id).ToString();
        string path = $"/api/dat-subscriptions/{identity}";
        using var holderScope = app.Services.CreateScope();
        var holder = holderScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            (await holder.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({catalog})) AS \"Value\"").SingleAsync()).ShouldBeTrue();

            (await ProblemAsync(await client.DeleteAsync(path), HttpStatusCode.Conflict))
                .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.Busy");
            (await db.DatSubscriptions.AnyAsync(subscription => subscription.Id == row.Id)).ShouldBeTrue();
        }

        (await client.DeleteAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await db.DatSubscriptions.AnyAsync(subscription => subscription.Id == row.Id)).ShouldBeFalse();
        (await ProblemAsync(await client.DeleteAsync(path), HttpStatusCode.NotFound))
            .GetProperty("errorCode").GetString().ShouldBe("DatEnrollment.NotFound");
    }

    [Fact]
    public void SubscriptionRoutes_RequireTheAdminPolicy()
    {
        var routes = AuthorizationInventory.GetRouteEndpoints(fixture.Services)
            .Where(endpoint => AuthorizationInventory.RoutePatternOf(endpoint) is var route
                && (route.StartsWith("/api/dat-subscriptions", StringComparison.Ordinal) || route == "/api/dat-catalogs"))
            .ToArray();
        routes.Length.ShouldBe(10);
        routes.ShouldAllBe(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy == AuthorizationPolicies.RequireAdmin));
    }
}
