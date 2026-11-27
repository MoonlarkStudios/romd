using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;
using DeliveryFixture = Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConsumerPlayLaterEndpointsTests
{
    private const string ListPath = "/api/me/play-later";

    [Fact]
    public async Task PlayLater_AcrossAccountsAndRequestSessions_PreservesOrderAndIsolatesMembership()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        await using (var db = fixture.CreateSetupDbContext())
        {
            db.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
            {
                LibraryId = seed.LibraryId,
                TitleId = seed.PartialTitleId,
                PlatformId = seed.PlatformId,
                IsVisible = true,
                IsOwned = true,
                EligibleReleaseCount = 1,
                ExposedReleaseCount = 1,
                Availability = "Partial"
            });
            await db.SaveChangesAsync();
        }

        var accountId = Guid.NewGuid();
        const string accountEmail = "play-later@localhost";
        using var client = fixture.CreateAuthenticatedClient(accountId, accountEmail, seed.LibraryId);
        using var secondSession = fixture.CreateAuthenticatedClient(accountId, accountEmail, seed.LibraryId);
        using var otherAccount = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "other-play-later@localhost", seed.LibraryId);
        string firstId = IdCoder.Encode(seed.OwnedTitleId);
        string secondId = IdCoder.Encode(seed.PartialTitleId);
        string membershipPath = $"{ListPath}/membership?titleIds={firstId}&titleIds={secondId}";

        (await client.PutAsync($"{ListPath}/{firstId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        (await client.PutAsync($"{ListPath}/{secondId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        (await client.PutAsync($"{ListPath}/{firstId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var shelfResponse = await secondSession.GetAsync($"{ListPath}?limit=1");
        shelfResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        shelfResponse.Headers.CacheControl!.Private.ShouldBeTrue();
        shelfResponse.Headers.CacheControl.NoStore.ShouldBeTrue();
        var shelf = (await shelfResponse.Content.ReadFromJsonAsync<Page<ConsumerTitleCard>>()).ShouldNotBeNull();
        shelf.Items.ShouldHaveSingleItem().Id.ShouldBe(secondId);
        shelf.HasNextPage.ShouldBeTrue();
        string nextPagePath = $"{ListPath}?limit=1&cursor={Uri.EscapeDataString(shelf.NextCursor.ShouldNotBeNull())}";
        var next = (await secondSession.GetFromJsonAsync<Page<ConsumerTitleCard>>(nextPagePath)).ShouldNotBeNull();
        next.Items.ShouldHaveSingleItem().Id.ShouldBe(firstId);
        next.HasNextPage.ShouldBeFalse();

        (await ReadMembershipAsync(secondSession, membershipPath)).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { firstId, secondId }.Order(StringComparer.Ordinal));
        (await ReadMembershipAsync(otherAccount, membershipPath)).ShouldBeEmpty();
        (await otherAccount.GetFromJsonAsync<Page<ConsumerTitleCard>>(ListPath)).ShouldNotBeNull().Items.ShouldBeEmpty();
        (await otherAccount.GetAsync(nextPagePath)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await otherAccount.DeleteAsync($"{ListPath}/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadMembershipAsync(secondSession, membershipPath)).ShouldContain(firstId);

        (await client.DeleteAsync($"{ListPath}/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"{ListPath}/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadMembershipAsync(secondSession, membershipPath)).ShouldBe([secondId]);
    }

    [Fact]
    public async Task PlayLater_WhenPublishedAccessIsRemovedAndRestored_HidesAndRecoversTheEntry()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "publication-play-later@localhost", seed.LibraryId);
        string titleId = IdCoder.Encode(seed.OwnedTitleId);
        string otherTitleId = IdCoder.Encode(seed.OtherLibraryTitleId);
        string path = $"{ListPath}/{titleId}";
        string membershipPath = $"{ListPath}/membership?titleIds={titleId}&titleIds={otherTitleId}";

        (await client.PutAsync(path, content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadMembershipAsync(client, membershipPath)).ShouldBe([titleId]);
        await using var db = fixture.CreateSetupDbContext();
        var publishedTitle = db.MaterializedLibraryTitles.Where(title =>
            title.LibraryId == seed.LibraryId && title.TitleId == seed.OwnedTitleId);
        var row = await publishedTitle.SingleAsync();
        (await publishedTitle.ExecuteDeleteAsync()).ShouldBe(1);

        using var hiddenResponse = await client.GetAsync(ListPath);
        var hidden = (await hiddenResponse.Content.ReadFromJsonAsync<Page<ConsumerTitleCard>>()).ShouldNotBeNull();
        hiddenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        hidden.Items.ShouldBeEmpty();
        hidden.HasNextPage.ShouldBeFalse();
        hidden.NextCursor.ShouldBeNull();
        (await ReadMembershipAsync(client, membershipPath)).ShouldBeEmpty();
        (await client.PutAsync(path, content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PutAsync($"{ListPath}/{otherTitleId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        db.MaterializedLibraryTitles.Add(row);
        await db.SaveChangesAsync();
        var restored = (await client.GetFromJsonAsync<Page<ConsumerTitleCard>>(ListPath)).ShouldNotBeNull();
        restored.Items.ShouldHaveSingleItem().Id.ShouldBe(titleId);
        (await ReadMembershipAsync(client, membershipPath)).ShouldBe([titleId]);
    }

    [Fact]
    public async Task PlayLaterMembership_InvalidTitleIdBatches_ReturnBadRequest()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "invalid-play-later@localhost", seed.LibraryId);
        string repeated = string.Join('&', Enumerable.Repeat($"titleIds={IdCoder.Encode(seed.OwnedTitleId)}", 101));

        (await client.GetAsync($"{ListPath}/membership")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"{ListPath}/membership?titleIds=%21")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"{ListPath}/membership?{repeated}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/membership?titleIds=abc")]
    [InlineData("PUT", "/abc")]
    [InlineData("DELETE", "/abc")]
    public async Task PlayLater_Anonymous_RequiresAuthentication(string method, string route)
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), ListPath + route);
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<string[]> ReadMembershipAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        using var body = (await response.Content.ReadFromJsonAsync<JsonDocument>()).ShouldNotBeNull();
        return body.RootElement.GetProperty("titleIds").EnumerateArray()
            .Select(value => value.GetString().ShouldNotBeNull()).ToArray();
    }
}
