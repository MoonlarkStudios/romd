using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ArtworkEnrichmentEndpointsTests(IntegrationTestFixture fixture)
{
    private const string Settings = "/api/enrichment/artwork/settings";

    [Fact]
    public async Task Settings_Anonymous_Unauthorized()
    {
        using var client = fixture.CreateClient();
        (await client.GetAsync(Settings)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Manager)]
    public async Task Settings_NonAdmin_Forbidden(RomdRoleType role)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [role]);
        (await client.GetAsync(Settings)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync(Settings, new UpdateArtworkEnrichmentSettingsRequest(Guid.Empty, false, false, false)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Settings_Conflict_LeavesExistingPolicyIntact()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var before = (await client.GetFromJsonAsync<ArtworkEnrichmentSettingsDto>(Settings))!;
        (await client.PutAsJsonAsync(Settings, new UpdateArtworkEnrichmentSettingsRequest(Guid.NewGuid(), !before.FillPosters, !before.FillHeroes, !before.FillLogos)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var after = (await client.GetFromJsonAsync<ArtworkEnrichmentSettingsDto>(Settings))!;
        after.FillPosters.ShouldBe(before.FillPosters);
        after.FillHeroes.ShouldBe(before.FillHeroes);
        after.FillLogos.ShouldBe(before.FillLogos);
    }

    [Fact]
    public async Task FillMissing_RepeatedRequests_QueueOneArtworkOnlyJobWithoutChangingMetadata()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var title = new TitleEntity { PlatformId = await db.Platforms.Select(x => x.Id).FirstAsync(),
            Name = "Artwork acquisition test " + Guid.NewGuid(), NormalizedName = Guid.NewGuid().ToString(),
            EnrichmentStatus = "Completed", Description = "Curated description", CreatedAt = DateTimeOffset.UtcNow };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        var titleId = IdCoder.Encode(title.Id);
        using var client = fixture.CreateAuthenticatedClient();
        (await client.PostAsync($"/api/enrichment/artwork/{titleId}", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsync($"/api/enrichment/artwork/{titleId}", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        db.ChangeTracker.Clear();
        var job = await db.Set<EnrichmentJobEntity>().SingleAsync(x => x.TitleId == title.Id);
        job.ArtworkOnly.ShouldBeTrue();
        job.ToDomain().ArtworkOnly.ShouldBeTrue();
        var preserved = await db.Titles.SingleAsync(x => x.Id == title.Id);
        preserved.Description.ShouldBe("Curated description");
        preserved.EnrichmentStatus.ShouldBe("Completed");
        var state = (await client.GetFromJsonAsync<ArtworkAcquisitionStateDto>($"/api/enrichment/artwork/{titleId}"))!;
        state.ActiveJobId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task FillMissing_OrdinaryUser_Forbidden()
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        (await client.PostAsync($"/api/enrichment/artwork/{IdCoder.Encode(1)}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
