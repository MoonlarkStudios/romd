using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

using DiscSetDefinition = Romd.Domain.Catalog.DiscSetDefinition;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class AdminDiscSetDecisionEndpointsTests
{
    [Fact]
    public async Task Decision_CommitKeepsWireShapeAndEtag_StaleReplayDoesNotChangeAcceptedRevision()
    {
        await using var host = new DecisionFixture();
        await host.InitializeAsync();
        var original = await SeedAsync(host);
        using var client = host.CreateAuthenticatedClient();
        string route = $"/api/disc-sets/{IdCoder.Encode(original.Id)}";
        using var get = await client.GetAsync(route);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        get.Headers.ETag!.Tag.ShouldBe($"\"{original.Revision}\"");
        using var request = Patch(route, "accept", get.Headers.ETag.Tag);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<DiscSet>()).ShouldNotBeNull();
        accepted.Id.ShouldBe(IdCoder.Encode(original.Id));
        accepted.CatalogSourceId.ShouldBe(IdCoder.Encode(original.SourceId));
        accepted.SystemKey.ShouldBe("psx");
        accepted.Decision.ShouldBe("accepted");
        accepted.Revision.ShouldNotBe(original.Revision);
        accepted.DecidedBy.ShouldBe(Guid.Parse(client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserIdHeader).Single()));
        accepted.Reason.ShouldBe("Reviewed");
        accepted.Correction.ShouldNotBeNull().Members.Single().ReleaseId.ShouldBe(IdCoder.Encode(original.ReleaseId));
        response.Headers.ETag!.Tag.ShouldBe($"\"{accepted.Revision}\"");
        using (var replay = Patch(route, "reject", $"\"{original.Revision}\""))
        using (var stale = await client.SendAsync(replay))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "DiscSet.PreconditionFailed"))
            stale.Headers.ETag.ShouldBeNull();
        using var read = await client.GetAsync(route);
        (await read.Content.ReadFromJsonAsync<DiscSet>())!.Revision.ShouldBe(accepted.Revision);
        read.Headers.ETag!.Tag.ShouldBe(response.Headers.ETag.Tag);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var stored = await db.DiscSets.SingleAsync(row => row.Id == original.Id);
        stored.Revision.ShouldBe(accepted.Revision);
        stored.Decision.ShouldBe("accepted");
        (await db.Libraries.SingleAsync(row => row.Id == original.LibraryId)).NeedsMaterialization.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, 428, "DiscSet.PreconditionRequired")]
    [InlineData("*", 428, "DiscSet.PreconditionRequired")]
    [InlineData("malformed", 400, "Request.InvalidParameter")]
    [InlineData("W/\"old\"", 412, "DiscSet.PreconditionFailed")]
    public async Task Decision_HeaderPrecedenceRejectsWithoutMutation(string? tag, int status, string code)
    {
        await using var host = new DecisionFixture();
        await host.InitializeAsync();
        var original = await SeedAsync(host);
        using var client = host.CreateAuthenticatedClient();
        using var request = Patch($"/api/disc-sets/{IdCoder.Encode(original.Id)}", tag?.StartsWith('W') == true ? "accept" : "INVALID", tag);

        using var response = await client.SendAsync(request);

        using (await ProblemEnvelope.ReadAsync(response, status, code))
        { }
        response.Headers.ETag.ShouldBeNull();
        await AssertUnchangedAsync(host, original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_AnonymousOrNonAdminCannotMutate(bool authenticated)
    {
        await using var host = new DecisionFixture();
        await host.InitializeAsync();
        var original = await SeedAsync(host);
        using var client = host.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), "contributor@localhost", [RomdRoleType.Contributor]);
        using var request = Patch($"/api/disc-sets/{IdCoder.Encode(original.Id)}", "accept", $"\"{original.Revision}\"");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        await AssertUnchangedAsync(host, original);
    }

    [Fact]
    public async Task Decision_ReviewRejectionRetainsExactProblemAndLeavesLibraryAndRevisionUnchanged()
    {
        await using var host = new DecisionFixture();
        await host.InitializeAsync();
        var original = await SeedAsync(host, needsReview: true);
        using var client = host.CreateAuthenticatedClient();
        using var request = Patch($"/api/disc-sets/{IdCoder.Encode(original.Id)}", "accept", $"\"{original.Revision}\"");

        using var response = await client.SendAsync(request);

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "DiscSet.Review");
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("Correct the review issues before accepting.");
        response.Headers.ETag.ShouldBeNull();
        await AssertUnchangedAsync(host, original);
    }

    private static HttpRequestMessage Patch(string route, string decision, string? tag)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, route)
        { Content = JsonContent.Create(new DecideDiscSetRequest(decision, null, null, null, "Reviewed")) };
        if (tag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", tag).ShouldBeTrue();
        return request;
    }

    private static async Task AssertUnchangedAsync(DecisionFixture host, Seed original)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var row = await db.DiscSets.SingleAsync(row => row.Id == original.Id);
        row.Revision.ShouldBe(original.Revision);
        row.Decision.ShouldBe("proposed");
        row.CorrectionJson.ShouldBeNull();
        row.DecidedBy.ShouldBeNull();
        (await db.Libraries.SingleAsync(row => row.Id == original.LibraryId)).NeedsMaterialization.ShouldBeFalse();
    }

    private sealed record Seed(int Id, Guid Revision, int SourceId, int ReleaseId, int LibraryId);
    private static async Task<Seed> SeedAsync(DecisionFixture host, bool needsReview = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var platform = await db.Platforms.SingleAsync(row => row.ShortName == "psx");
        var title = new TitleEntity { PlatformId = platform.Id, Name = "Decision game", NormalizedName = "decision game", EnrichmentStatus = "Completed" };
        var source = new CatalogSourceEntity { Kind = "Dat", Status = "Active" };
        db.Titles.Add(title);
        db.CatalogSources.Add(source);
        await db.SaveChangesAsync();
        var entry = new SourceEntryEntity { CatalogSourceId = source.Id, PlatformId = platform.Id, EntryKey = "disc", Name = "Disc" };
        var release = new CatalogReleaseEntity { PlatformId = platform.Id, CatalogTitleId = title.Id, Fingerprint = "fp:decision", Name = "Disc" };
        db.SourceEntries.Add(entry);
        db.CatalogReleases.Add(release);
        await db.SaveChangesAsync();
        db.CatalogReleaseSources.Add(new() { SourceEntryId = entry.Id, CatalogReleaseId = release.Id, ProviderClaimKey = "decision", AssertedTitleId = title.Id });
        var definition = new DiscSetDefinition("Game", 1, [new("disc", "Disc", "fp:decision", 1, title.Id)],
            needsReview ? ["NeedsReview"] : [], "decision-test");
        var row = new DiscSetEntity { PlatformId = platform.Id, CatalogSourceId = source.Id, ProposalKey = "decision", TitleIds = [title.Id], ProposalJson = JsonSerializer.Serialize(definition) };
        db.DiscSets.Add(row);
        await db.SaveChangesAsync();
        var library = new LibraryEntity
        {
            Name = "Selected set",
            ConfigurationState = "Valid",
            NeedsMaterialization = false,
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration { SelectedDiscSetIds = [row.Id] })
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return new(row.Id, row.Revision, source.Id, release.Id, library.Id);
    }

    private sealed class DecisionFixture : IntegrationTestFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
