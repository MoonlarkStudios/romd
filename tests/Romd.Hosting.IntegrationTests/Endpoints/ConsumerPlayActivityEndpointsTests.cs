using System.Net;
using System.Net.Http.Json;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Activity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerPlayActivityEndpointsTests
{
    [Fact]
    public async Task SessionLifecycle_IsRetrySafeUserScopedAndHardDeleted()
    {
        await using var fixture = await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        var userId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var sessionId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        using var client = fixture.CreateAuthenticatedClient(userId, "activity@localhost", seed.LibraryId);
        using var other = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "other-activity@localhost", seed.LibraryId);
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var open = Request(seed, startedAt);
        string path = $"/api/me/activity/play-sessions/{sessionId}";

        (await client.PutAsJsonAsync(path, open)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(path, open)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var endedAt = startedAt.AddMinutes(5);
        var closed = open with { EndedAt = endedAt, ActiveDurationSeconds = 240 };
        var closedResponse = await client.PutAsJsonAsync(path, closed);
        closedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await closedResponse.Content.ReadFromJsonAsync<PlaySessionDto>();
        stored.ShouldNotBeNull();
        stored.SessionId.ShouldBe(sessionId);
        stored.ClientId.ShouldBe("org.example.third-party");
        stored.EndedAt.ShouldBe(endedAt);
        stored.ActiveDurationSeconds.ShouldBe(240);

        // A late open retry is accepted but cannot reopen the ended session.
        var lateOpen = await client.PutAsJsonAsync(path, open);
        (await lateOpen.Content.ReadFromJsonAsync<PlaySessionDto>())!.EndedAt.ShouldBe(endedAt);
        var conflict = await client.PutAsJsonAsync(
            path,
            closed with { EndedAt = endedAt.AddMinutes(1) });
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var page = await client.GetFromJsonAsync<Page<PlaySessionDto>>("/api/me/activity/play-sessions?limit=1");
        page.ShouldNotBeNull();
        page.Items.Select(item => item.SessionId).ShouldBe([sessionId]);
        var recent = await client.GetFromJsonAsync<IReadOnlyList<RecentlyPlayedTitleDto>>(
            "/api/me/activity/recently-played");
        recent.ShouldNotBeNull();
        recent.Single().Id.ShouldBe(IdCoder.Encode(seed.OwnedTitleId));
        recent.Single().PlayCount.ShouldBe(1);

        (await client.DeleteAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Deletion leaves no tombstone: the same compatible client UUID can be stored again.
        (await client.PutAsJsonAsync(path, closed)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.DeleteAsync("/api/me/activity/play-sessions")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var empty = await client.GetFromJsonAsync<Page<PlaySessionDto>>("/api/me/activity/play-sessions");
        empty!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_RejectsInaccessibleReleaseAndInvalidTimes()
    {
        await using var fixture = await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "invalid-activity@localhost", seed.LibraryId);
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        string path = $"/api/me/activity/play-sessions/{Guid.NewGuid()}";

        var inaccessible = Request(seed, startedAt) with
        {
            TitleId = IdCoder.Encode(seed.NonExposedTitleId),
            ReleaseId = IdCoder.Encode(seed.NonExposedReleaseId)
        };
        (await client.PutAsJsonAsync(path, inaccessible)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(
            path,
            Request(seed, startedAt) with { EndedAt = startedAt.AddSeconds(-1) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("GET", "/api/me/activity/play-sessions")]
    [InlineData("GET", "/api/me/activity/recently-played")]
    [InlineData("DELETE", "/api/me/activity/play-sessions")]
    public async Task ActivityEndpoints_RequireAuthentication(string method, string path)
    {
        await using var fixture = await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static UpsertPlaySessionRequest Request(
        ConsumerDeliveryEndpointsTests.DeliverySeed seed,
        DateTimeOffset startedAt) => new()
    {
        ClientId = "org.example.third-party",
        TitleId = IdCoder.Encode(seed.OwnedTitleId),
        ReleaseId = IdCoder.Encode(seed.OwnedReleaseId),
        StartedAt = startedAt
    };
}
