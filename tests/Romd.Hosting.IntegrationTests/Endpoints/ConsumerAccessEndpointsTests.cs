using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Consumer.Server;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerAccessEndpointsTests
{
    [Fact]
    public async Task GetConsumerReleaseAccess_AllowedAndRevoked_ReturnExactMetadataSafeShapes()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "access@localhost",
            seed.LibraryId);

        var allowedResponse = await GetAccessAsync(client, seed.OwnedReleaseId);
        string allowedJson = await allowedResponse.Content.ReadAsStringAsync();
        using var allowedDocument = JsonDocument.Parse(allowedJson);
        var allowed = allowedDocument.RootElement;

        allowedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        allowed.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["allowed", "releaseId", "serverInstanceId", "titleId"]);
        allowed.GetProperty("serverInstanceId").GetString().ShouldSatisfyAllConditions(
            value => value.ShouldNotBeNull(),
            value => Guid.TryParseExact(value, "D", out _).ShouldBeTrue(),
            value => value.ShouldBe(value!.ToLowerInvariant()));
        allowed.GetProperty("releaseId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedReleaseId));
        allowed.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        allowed.GetProperty("titleId").GetString().ShouldBe(IdCoder.Encode(seed.OwnedTitleId));

        var revokedResponse = await GetAccessAsync(client, seed.NonExposedReleaseId);
        string revokedJson = await revokedResponse.Content.ReadAsStringAsync();
        using var revokedDocument = JsonDocument.Parse(revokedJson);
        var revoked = revokedDocument.RootElement;

        revokedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        revoked.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["allowed", "releaseId", "serverInstanceId"]);
        revoked.GetProperty("releaseId").GetString().ShouldBe(IdCoder.Encode(seed.NonExposedReleaseId));
        revoked.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        revoked.TryGetProperty("titleId", out _).ShouldBeFalse();
        revokedJson.ShouldNotContain("Mega Man", Case.Insensitive);
        revokedJson.ShouldNotContain("title", Case.Insensitive);
    }

    [Theory]
    [InlineData("not-valid!")]
    public async Task GetConsumerReleaseAccess_MalformedReleaseId_ReturnsBadRequest(string releaseId)
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "access@localhost");

        var response = await client.GetAsync($"/api/releases/{releaseId}/access");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_NonCanonicalReleaseId_ReturnsBadRequest()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "access@localhost");
        // Raw Sqids decodes "eED9Qbk" to 42, whose canonical encoding is "eD9Qbk".
        IdCoder.Encode(42).ShouldBe("eD9Qbk");

        var response = await client.GetAsync("/api/releases/eED9Qbk/access");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty("releaseId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_Unauthenticated_ReturnsUnauthorized()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await GetAccessAsync(client, 1);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_InvalidBearerToken_ReturnsInvalidTokenChallenge()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-valid-access-token");

        var response = await GetAccessAsync(client, 1);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.ShouldBe("Bearer");
        string parameter = challenge.Parameter
            ?? throw new InvalidOperationException("Bearer challenge had no parameters.");
        parameter.ShouldContain("error=\"invalid_token\"");
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_NoInvalidOrUnmaterializedLibrary_ReturnsConflictNotRevoke()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        int pendingLibraryId = await fixture.CreateLibraryAsync("Pending Access", needsMaterialization: true);
        using var unassigned = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "none@localhost");
        using var invalid = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "invalid@localhost",
            seed.InvalidLibraryId);
        using var pending = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "pending@localhost",
            pendingLibraryId);

        foreach (HttpClient client in new[] { unassigned, invalid, pending })
        {
            var response = await GetAccessAsync(client, seed.OwnedReleaseId);
            string problem = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            problem.ShouldNotContain("Chrono Trigger", Case.Insensitive);
            problem.ShouldNotContain(IdCoder.Encode(seed.OwnedTitleId), Case.Insensitive);
        }
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_PublishedLibraryWithPendingChanges_KeepsPublishedAccess()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.MarkPendingAccessChangesAsync(seed.LibraryId);
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "pending-published@localhost",
            seed.LibraryId);

        var response = await GetAccessAsync(client, seed.OwnedReleaseId);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.RootElement.GetProperty("allowed").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"ContentRatingPolicy\":{\"MaxMinimumAge\":-1}}")]
    public async Task GetConsumerReleaseAccess_StaleValidButInvalidConfiguration_ReturnsConflictWithoutMetadata(
        string configurationJson)
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.CorruptLibraryConfigurationAsync(
            seed.LibraryId,
            configurationJson,
            LibraryConfigurationState.Valid.ToString());
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "corrupt-config@localhost",
            seed.LibraryId);

        var response = await GetAccessAsync(client, seed.OwnedReleaseId);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        AssertNoRestrictedMetadata(body, seed);
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("title")]
    [InlineData("platform")]
    [InlineData("duplicate")]
    public async Task GetConsumerReleaseAccess_CorruptProjection_ReturnsConflictWithoutMetadata(
        string corruption)
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        int requestedReleaseId = seed.OwnedReleaseId;
        switch (corruption)
        {
            case "orphan":
                requestedReleaseId = await fixture.OrphanReleaseProjectionAsync(
                    seed.LibraryId,
                    seed.OwnedReleaseId);
                break;
            case "title":
                await fixture.MismatchReleaseProjectionTitleAsync(
                    seed.LibraryId,
                    seed.OwnedReleaseId,
                    seed.NonExposedTitleId);
                break;
            case "platform":
                await fixture.MismatchReleaseProjectionPlatformAsync(
                    seed.LibraryId,
                    seed.OwnedReleaseId);
                break;
            case "duplicate":
                await fixture.DuplicateReleaseProjectionAsync(
                    seed.LibraryId,
                    seed.OwnedReleaseId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            $"corrupt-{corruption}@localhost",
            seed.LibraryId);

        var response = await GetAccessAsync(client, requestedReleaseId);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        AssertNoRestrictedMetadata(body, seed);
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_TwoLibrariesDifferForSameRelease()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var first = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "first@localhost",
            seed.LibraryId);
        using var second = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "second@localhost",
            seed.OtherLibraryId);

        var firstDecision = await ReadAccessAsync(await GetAccessAsync(first, seed.OwnedReleaseId));
        var secondDecision = await ReadAccessAsync(await GetAccessAsync(second, seed.OwnedReleaseId));

        firstDecision.Allowed.ShouldBeTrue();
        firstDecision.TitleId.ShouldBe(IdCoder.Encode(seed.OwnedTitleId));
        secondDecision.Allowed.ShouldBeFalse();
        secondDecision.TitleId.ShouldBeNull();
    }

    [Fact]
    public async Task GetConsumerReleaseAccess_RealIssuedTokenAfterLiveReassignment_UsesNewLibraryWithoutReissue()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var issued = await fixture.IssueDeviceAccessTokenAsync(
            "real-access-user",
            "real-access@example.test",
            "Password123",
            seed.LibraryId);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.AccessToken);

        var before = await ReadAccessAsync(await GetAccessAsync(client, seed.OwnedReleaseId));
        await fixture.ReassignUserAsync(issued.User.Id, seed.OtherLibraryId);
        var formerRelease = await ReadAccessAsync(await GetAccessAsync(client, seed.OwnedReleaseId));
        var newRelease = await ReadAccessAsync(await GetAccessAsync(client, seed.OtherLibraryReleaseId));

        before.Allowed.ShouldBeTrue();
        formerRelease.Allowed.ShouldBeFalse();
        newRelease.Allowed.ShouldBeTrue();
        newRelease.TitleId.ShouldBe(IdCoder.Encode(seed.OtherLibraryTitleId));
    }

    [Fact]
    public async Task IdentityAccessAndManifest_ReturnSameServerInstanceAndReleaseTitleIdentity()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "identity@localhost",
            seed.LibraryId);

        var identity = await client.GetFromJsonAsync<ConsumerServerIdentity>("/api/server/identity");
        var access = await ReadAccessAsync(await GetAccessAsync(client, seed.OwnedReleaseId));
        var manifestResponse = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<ConsumerReleaseManifest>(ConsumerDeliveryEndpointsTests.ConsumerJsonOptions);

        identity.ShouldNotBeNull();
        manifestResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        manifest.ShouldNotBeNull();
        access.ServerInstanceId.ShouldBe(identity.InstanceId);
        manifest.ServerInstanceId.ShouldBe(identity.InstanceId);
        access.ReleaseId.ShouldBe(manifest.ReleaseId);
        access.TitleId.ShouldBe(manifest.TitleId);
    }

    private static Task<HttpResponseMessage> GetAccessAsync(HttpClient client, int releaseId) =>
        client.GetAsync($"/api/releases/{IdCoder.Encode(releaseId)}/access");

    private static void AssertNoRestrictedMetadata(
        string responseBody,
        ConsumerDeliveryEndpointsTests.DeliverySeed seed)
    {
        responseBody.ShouldNotContain("Chrono Trigger", Case.Insensitive);
        responseBody.ShouldNotContain(IdCoder.Encode(seed.OwnedTitleId), Case.Insensitive);
        responseBody.ShouldNotContain("\"titleId\"", Case.Insensitive);
        responseBody.ShouldNotContain("\"name\"", Case.Insensitive);
        responseBody.ShouldNotContain("artwork", Case.Insensitive);
    }

    private static async Task<ConsumerReleaseAccess> ReadAccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ConsumerReleaseAccess>();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return body.ShouldNotBeNull();
    }
}
