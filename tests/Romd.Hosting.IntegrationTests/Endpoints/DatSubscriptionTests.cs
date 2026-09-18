using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence.Subscriptions;
using Romd.Persistence;
using Shouldly;
using Xunit;

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
        public Task<ErrorOr<byte[]>> FetchPlayStationAsync(CancellationToken ct) => Task.FromResult(Result);
    }

    [Fact]
    public async Task Subscription_CheckRetainsCandidateWithoutClaims_ApprovalCommitsOnceAndSurvivesPublisherFailure()
    {
        using var authenticated = fixture.CreateAuthenticatedClient();
        const string first = "<game name='Subscription First'><rom name='one.bin' size='1' crc='11111111'/></game>";
        const string added = "<game name='Subscription Second'><rom name='two.bin' size='2' crc='22222222'/></game>";
        static byte[] Document(string entries) => Encoding.UTF8.GetBytes($"<datafile><header><name>Sony - PlayStation</name></header>{entries}</datafile>");
        var fake = new CandidateClient { Result = Document(first) };
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ISignedDatCatalogClient>(fake)));
        using var client = app.CreateClient();
        foreach (var header in authenticated.DefaultRequestHeaders) client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        using var uploadFile = TestHelpers.CreateMultipartContent(Encoding.UTF8.GetString(Document(first)), "subscription.dat");
        var upload = await client.PostAsync("/api/upload/dat", uploadFile);
        upload.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var accepted = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        await TestHelpers.WaitForJobCompletionAsync(client, accepted.RootElement.GetProperty("jobId").GetString()!);
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RomdDbContext>();
        var original = await db.DatFiles.SingleAsync(d => d.Name == "Sony - PlayStation" && d.Lifecycle == "Active");
        var service = new DatSubscriptionService(db, sp.GetRequiredService<IDatRepository>(), sp.GetRequiredService<IFileStorageService>(),
            sp.GetRequiredService<IDatReplacementReview>(), fake, TimeProvider.System);
        (await service.GetAsync(original.Id, default)).Value.Subscribed.ShouldBeFalse();
        string publicId = await TestHelpers.GetDatIdByNameAsync(client, "Sony - PlayStation");
        var checkResponse = await client.PostAsync($"/api/dats/{publicId}/subscription/check", null);
        checkResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await checkResponse.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatSubscriptionStatus>())!.State.ShouldBe("UpToDate");
        using var anonymous = app.CreateClient();
        (await anonymous.PostAsync($"/api/dats/{publicId}/subscription/check", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        int versions = await db.DatFiles.CountAsync();
        int claims = await db.SourceEntries.CountAsync();
        int jobs = await db.Jobs.CountAsync();
        fake.Result = Document(first + added);
        (await service.CheckAsync(original.Id, default)).Value.State.ShouldBe("UpdateAvailable");
        var row = await db.DatSubscriptions.SingleAsync(s => s.DatSourceId == original.DatSourceId);
        var fileRepo = sp.GetRequiredService<IFileRepository>();
        (await fileRepo.IsReferencedAsync(row.CandidateFileId!.Value, default)).ShouldBeTrue();
        (await fileRepo.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddDays(1), default)).ShouldNotContain(row.CandidateFileId.Value);
        var previewResponse = await client.PostAsync($"/api/dats/{publicId}/subscription/preview", null);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatReplacementPreview>())!;
        preview.EntriesAdded.ShouldBe(1);
        var savedChanges = await client.PostAsJsonAsync($"/api/dats/{publicId}/subscription/changes",
            new { preview.ActiveSha256, preview.CandidateSha256, change = "Added" });
        savedChanges.StatusCode.ShouldBe(HttpStatusCode.OK, await savedChanges.Content.ReadAsStringAsync());
        (await savedChanges.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatChangePage>())!.Total.ShouldBe(1);
        using var manualChanges = TestHelpers.CreateMultipartContent(Encoding.UTF8.GetString(Document(first + added)), "review.dat");
        manualChanges.Add(new StringContent(preview.ActiveSha256), "activeSha256");
        manualChanges.Add(new StringContent(preview.CandidateSha256), "candidateSha256");
        manualChanges.Add(new StringContent("0"), "offset");
        var manualPage = await client.PostAsync($"/api/dats/{publicId}/replacement-changes", manualChanges);
        manualPage.StatusCode.ShouldBe(HttpStatusCode.OK, await manualPage.Content.ReadAsStringAsync());
        (await manualPage.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatChangePage>())!.Entries.Single().Name.ShouldBe("Subscription Second");
        (await db.DatFiles.CountAsync()).ShouldBe(versions);
        (await db.SourceEntries.CountAsync()).ShouldBe(claims);
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        fake.Result = Error.Failure("Test.Upstream", "Publisher unavailable; retry.");
        (await service.CheckAsync(original.Id, default)).Value.State.ShouldBe("CheckFailed");
        (await db.DatFiles.SingleAsync(d => d.Id == original.Id)).Lifecycle.ShouldBe("Active");
        (await service.ApplyAsync(original.Id, preview.ActiveSha256, preview.CandidateSha256, default)).IsError.ShouldBeTrue();
        fake.Result = Encoding.UTF8.GetBytes("<html>bad candidate</html>");
        (await service.CheckAsync(original.Id, default)).Value.State.ShouldBe("CheckFailed");
        fake.Result = Document(first + added);
        (await service.CheckAsync(original.Id, default)).Value.State.ShouldBe("UpdateAvailable");
        (await service.ApplyAsync(original.Id, new string('0',64), preview.CandidateSha256, default)).IsError.ShouldBeTrue();
        var apply = await service.ApplyAsync(original.Id, preview.ActiveSha256, preview.CandidateSha256, default);
        apply.IsError.ShouldBeFalse();
        var repeat = await service.ApplyAsync(original.Id, preview.ActiveSha256, preview.CandidateSha256, default);
        repeat.Value.JobId.ShouldBe(apply.Value.JobId);
        (await db.JobDispatches.CountAsync(d => d.JobId == apply.Value.JobId)).ShouldBe(1);
        await TestHelpers.WaitForJobCompletionAsync(client, apply.Value.JobId.ToString());
        (await service.GetAsync(original.Id, default)).Value.State.ShouldBe("UpToDate");
        (await db.DatFiles.SingleAsync(d => d.DatSourceId == original.DatSourceId && d.Lifecycle == "Active")).GameCount.ShouldBe(2);
    }
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
        var fake = new CandidateClient { Result = Document(false), Catalogs = [new(catalogId, systemId, name, provider, "healthy", new string('a',64), initialCount, initialCount, null)] };
        using var authenticated = fixture.CreateAuthenticatedClient();
        using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ISignedDatCatalogClient>(fake)));
        using var client = app.CreateClient();
        foreach (var header in authenticated.DefaultRequestHeaders) client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync("/api/dat-subscriptions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var directory = await client.GetFromJsonAsync<Romd.Contracts.Management.Models.DatCatalogDirectoryDto>("/api/dat-subscriptions");
        directory!.Catalogs.Single().CatalogId.ShouldBe(catalogId);
        async Task<Romd.Contracts.Management.Models.DatCatalogSubscriptionDto> Check()
        {
            var result = await client.PostAsJsonAsync("/api/dat-subscriptions/check", new { catalogId });
            result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatCatalogSubscriptionDto>())!;
        }
        var subscription = await Check();
        subscription.State.ShouldBe("ReadyToImport");
        subscription.ActiveDatId.ShouldBeNull();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.DatFiles.CountAsync(d => d.Name == name)).ShouldBe(0);
        var jobs = await db.Jobs.CountAsync();
        async Task<Romd.Contracts.Management.Models.DatReplacementPreview> Preview()
        {
            var result = await client.PostAsync($"/api/dat-subscriptions/{subscription.Id}/preview", null);
            result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatReplacementPreview>())!;
        }
        async Task<string> Apply(Romd.Contracts.Management.Models.DatReplacementPreview preview)
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
        var changesResponse = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/changes",
            new { first.ActiveSha256, first.CandidateSha256, offset = 0 });
        changesResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await changesResponse.Content.ReadAsStringAsync());
        var changes = (await changesResponse.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatChangePage>())!;
        changes.Total.ShouldBe(initialCount); changes.Entries.ShouldContain(e => e.Name == "Enrollment One");
        (await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/changes",
            new { first.ActiveSha256, candidateSha256 = new string('0', 64) })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var detailsResponse = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/changes",
            new { first.ActiveSha256, first.CandidateSha256, entryName = "Enrollment One" });
        detailsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var details = (await detailsResponse.Content.ReadFromJsonAsync<Romd.Contracts.Management.Models.DatChangePage>())!;
        details.Files.Single().Fields.Single(f => f.Field == "Size").After.ShouldBe("1");
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
        var initialJob = await Apply(first);
        (await Apply(first)).ShouldBe(initialJob);
        await TestHelpers.WaitForJobCompletionAsync(client, initialJob);
        var status = await client.GetFromJsonAsync<Romd.Contracts.Management.Models.DatCatalogSubscriptionDto>($"/api/dat-subscriptions/{subscription.Id}");
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
        fake.Result = Document(true);
        (await Check()).State.ShouldBe("UpdateAvailable");
        var update = await Preview();
        update.EntriesAdded.ShouldBe(1);
        update.EntriesRemoved.ShouldBe(0);
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
    }

}
