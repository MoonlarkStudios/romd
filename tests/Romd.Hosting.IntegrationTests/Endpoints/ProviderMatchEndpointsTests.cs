using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ProviderMatchEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Anonymous_CannotReadProviderMatches()
    {
        using var client = fixture.CreateClient();
        (await client.GetAsync($"/api/provider-matches/titles/{IdCoder.Encode(1)}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OrdinaryUser_CannotChangeProviderMatches()
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        (await client.PostAsJsonAsync("/api/provider-matches/igdb/search", new { query = "Metroid" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_CanReadDirectoryWithoutAdminSettingsAccess()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var title = new Romd.Persistence.Entities.TitleEntity { PlatformId = await db.Platforms.Select(x => x.Id).FirstAsync(), Name = "Provider test", NormalizedName = Guid.NewGuid().ToString(), EnrichmentStatus = "None", CreatedAt = DateTimeOffset.UtcNow };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);
        var response = await client.GetAsync($"/api/provider-matches/titles/{IdCoder.Encode(title.Id)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("clientSecret", Case.Insensitive);
        body.ShouldNotContain("apiKey", Case.Insensitive);
    }
}
