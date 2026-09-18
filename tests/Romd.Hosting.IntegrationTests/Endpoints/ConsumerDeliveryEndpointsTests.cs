using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Delivery;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Delivery;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Consumer.Server;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerDeliveryEndpointsTests
{
    private const long UnsafeJavaScriptInteger = 9_007_199_254_740_993;
    private const string TestJwtAudience = "RomdConsumerDeliveryEndpointTests";
    private const string TestJwtIssuer = "RomdConsumerDeliveryEndpointTests";
    private const string TestJwtSecret = "consumer-delivery-test-secret-at-least-32-characters";
    private const string DeliverySigningSecret = "consumer-delivery-integration-signing-secret";

    [Fact]
    public async Task IssueReleaseManifest_OwnedRelease_ReturnsManifestWithRedeemableContentGrant()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);
        string json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConsumerReleaseManifestDto>(json, JsonOptions);
        var identity = await client.GetFromJsonAsync<ConsumerServerIdentityDto>("/api/server/identity");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        identity.ShouldNotBeNull();
        body.ServerInstanceId.ShouldBe(identity.InstanceId);
        body.ReleaseId.ShouldBe(IdCoder.Encode(seed.OwnedReleaseId));
        body.TitleId.ShouldBe(IdCoder.Encode(seed.OwnedTitleId));

        body.SystemKey.ShouldBe("snes");
        body.IsComplete.ShouldBeTrue();
        body.Runtime.ContentType.ShouldBe("single_rom");
        body.Runtime.Launch.ShouldNotBeNull();
        body.Runtime.Launch.Type.ShouldBe("file");
        body.Runtime.Launch.RelativePath.ShouldBe("chrono/chrono-trigger.sfc");
        body.Runtime.Packaging.ShouldBe("direct_files");
        body.Runtime.MinimumInstallBytes.ShouldBe((ByteCount)seed.ContentSizeBytes);
        var item = body.Items.ShouldHaveSingleItem();
        item.RelativePath.ShouldBe("chrono/chrono-trigger.sfc");
        item.Role.ShouldBe("rom");
        item.SizeBytes.ShouldBe((ByteCount)seed.ContentSizeBytes);
        item.Sha256.ShouldBe(seed.Sha256.ToString());
        item.IsAvailable.ShouldBeTrue();
        item.ContentGrant.ShouldNotBeNull();
        item.ContentGrant.DownloadUrl.ShouldStartWith("/delivery/content/");
        item.ContentGrant.ExpiresAt.ShouldBeGreaterThan(fixture.TimeProvider.GetUtcNow());

        using var wireDocument = JsonDocument.Parse(json);
        wireDocument.RootElement.GetProperty("runtime").GetProperty("minimumInstallBytes")
            .ValueKind.ShouldBe(JsonValueKind.String);
        wireDocument.RootElement.GetProperty("items")[0].GetProperty("sizeBytes")
            .ValueKind.ShouldBe(JsonValueKind.String);

        json.ShouldNotContain("\"fileId\"", Case.Insensitive);
        // Opaque grant tokens can contain the letters "cas" by chance. Assert
        // storage fields and paths, not arbitrary substrings inside signed data.
        json.ShouldNotContain("\"cas\"", Case.Insensitive);
        json.ShouldNotContain("/cas/", Case.Insensitive);
        json.ShouldNotContain("\"storagePath\"", Case.Insensitive);

        var validatedGrant = fixture.ValidateGrant(ExtractGrantToken(item.ContentGrant.DownloadUrl));
        validatedGrant.Grant.UserId.ShouldBe(userId);
        validatedGrant.Grant.LibraryId.ShouldBe(seed.LibraryId);
        validatedGrant.Grant.TitleId.ShouldBe(seed.OwnedTitleId);
        validatedGrant.Grant.ReleaseId.ShouldBe(seed.OwnedReleaseId);
        validatedGrant.Grant.RomId.ShouldBe(seed.RomId);
        validatedGrant.Grant.FileId.ShouldBe(seed.FileId);
        validatedGrant.Grant.Sha256.ShouldBe(seed.Sha256);
        validatedGrant.Grant.SizeBytes.ShouldBe(seed.ContentSizeBytes);

        using var anonymousClient = fixture.Factory.CreateClient();
        var grantRouteResponse = await anonymousClient.GetAsync(item.ContentGrant.DownloadUrl);
        byte[] content = await grantRouteResponse.Content.ReadAsByteArrayAsync();

        grantRouteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        grantRouteResponse.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        grantRouteResponse.Content.Headers.ContentLength.ShouldBe(seed.ContentSizeBytes);
        grantRouteResponse.Headers.CacheControl.ShouldNotBeNull();
        grantRouteResponse.Headers.CacheControl.Private.ShouldBeTrue();
        grantRouteResponse.Headers.CacheControl.NoStore.ShouldBeTrue();
        grantRouteResponse.Headers.ShouldNotContain(header =>
            header.Key.Equals("Accept-Ranges", StringComparison.OrdinalIgnoreCase));
        grantRouteResponse.Content.Headers.ShouldNotContain(header =>
            header.Key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase));
        content.ShouldBe(seed.ContentBytes);
    }

    [Fact]
    public async Task IssueReleaseManifest_UnsafeInt64Sizes_AreExactCanonicalStringsOnTheWire()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await context.CatalogReleaseFiles
                .Where(file => file.CatalogReleaseId == seed.OwnedReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.Size, UnsafeJavaScriptInteger));
        }

        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            "unsafe-int64@localhost",
            seed.LibraryId);

        using var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("runtime").GetProperty("minimumInstallBytes")
            .GetString().ShouldBe(UnsafeJavaScriptInteger.ToString());
        document.RootElement.GetProperty("items")[0].GetProperty("sizeBytes")
            .GetString().ShouldBe(UnsafeJavaScriptInteger.ToString());
    }

    [Fact]
    public async Task RedeemContentGrant_RangeRequest_ReturnsFullContentWithoutRangeHeaders()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        string downloadUrl = await IssueOwnedManifestGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Range = new RangeHeaderValue(0, 1);

        var response = await client.SendAsync(request);
        byte[] content = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldNotBe(HttpStatusCode.PartialContent);
        response.Headers.ShouldNotContain(header =>
            header.Key.Equals("Accept-Ranges", StringComparison.OrdinalIgnoreCase));
        response.Content.Headers.ShouldNotContain(header =>
            header.Key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase));
        content.ShouldBe(seed.ContentBytes);
    }

    [Fact]
    public async Task RedeemContentGrant_TamperedToken_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        string downloadUrl = await IssueOwnedManifestGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/delivery/content/{ReplaceFirstSignatureCharacter(ExtractGrantToken(downloadUrl))}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RedeemContentGrant_ExpiredToken_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        string downloadUrl = await IssueOwnedManifestGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var response = await client.GetAsync(downloadUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RedeemContentGrant_UnknownKey_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.Factory.CreateClient();
        string token = CreateGrantToken(seed, fixture.TimeProvider, signingKeyId: "unknown-key");

        var response = await client.GetAsync($"/delivery/content/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("payload.signature.extra")]
    public async Task RedeemContentGrant_MalformedToken_ReturnsUnauthorized(string token)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/delivery/content/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RedeemContentGrant_ValidGrantWithMissingCasObject_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.Factory.CreateClient();
        string token = CreateGrantToken(seed, fixture.TimeProvider, sha256: NewSha256(42), sizeBytes: 42);

        var response = await client.GetAsync($"/delivery/content/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConsumerStorageRoutes_DoNotServeTheOtherLaneContent()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        string downloadUrl = await IssueOwnedManifestGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        var mediaRouteResponse = await client.GetAsync($"/media/{ExtractGrantToken(downloadUrl)}");
        var contentRouteResponse = await client.GetAsync($"/delivery/content/{IdCoder.Encode(1)}");

        mediaRouteResponse.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        contentRouteResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IssueReleaseManifest_LaunchOptionRoute_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/titles/{IdCoder.Encode(seed.OwnedTitleId)}/launch-options/InstallRequired/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueReleaseManifest_NonExposedRelease_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.NonExposedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueReleaseManifest_ExposedUnownedRelease_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.ExposedUnownedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueReleaseManifest_PartialMultiFileRelease_ReturnsUnavailableItems()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.PartialReleaseId)}/manifest",
            content: null);
        string json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConsumerReleaseManifestDto>(json, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.IsComplete.ShouldBeFalse();

        body.SystemKey.ShouldBe("snes");
        body.Runtime.ContentType.ShouldBe("unknown");
        body.Runtime.Launch.ShouldBeNull();
        body.Runtime.Packaging.ShouldBe("direct_files");
        body.Runtime.MinimumInstallBytes.ShouldBe((ByteCount)(seed.ContentSizeBytes + 32));
        body.Items.Count.ShouldBe(3);
        body.Items.ShouldAllBe(item => item.Role == "rom");
        var availableItem = body.Items.Single(item => item.IsAvailable);
        availableItem.RelativePath.ShouldBe("partial/part-1.bin");
        availableItem.SizeBytes.ShouldBe((ByteCount)seed.ContentSizeBytes);
        availableItem.Sha256.ShouldBe(seed.Sha256.ToString());
        availableItem.ContentGrant.ShouldNotBeNull();
        availableItem.ContentGrant.DownloadUrl.ShouldStartWith("/delivery/content/");

        body.Items.Count(item => !item.IsAvailable).ShouldBe(2);
        foreach (var item in body.Items.Where(item => !item.IsAvailable))
        {
            item.RelativePath.ShouldStartWith("partial/part-");
            item.SizeBytes.ShouldBe((ByteCount)16);
            item.Sha256.ShouldBeNull();
            item.ContentGrant.ShouldBeNull();
        }
    }

    [Fact]
    public async Task IssueReleaseManifest_WrongLibraryRelease_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OtherLibraryReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueReleaseManifest_UserWithoutLibraryScope_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost");

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueReleaseManifest_InvalidLibraryConfiguration_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.InvalidLibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("title")]
    [InlineData("platform")]
    public async Task IssueReleaseManifest_CorruptProjection_ReturnsNotFoundWithoutContentGrant(
        string corruption)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
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
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
        using var client = fixture.CreateAuthenticatedClient(
            Guid.NewGuid(),
            $"corrupt-{corruption}@localhost",
            seed.LibraryId);

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(requestedReleaseId)}/manifest",
            content: null);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("Chrono Trigger", Case.Insensitive);
        body.ShouldNotContain("contentGrant", Case.Insensitive);
        body.ShouldNotContain("/delivery/content/", Case.Insensitive);
    }

    [Fact]
    public async Task IssueReleaseManifest_UnauthenticatedRequest_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPlatformBios_ExposedPlatform_ReturnsPerFileItemsWithRedeemableGrant()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync("/api/systems/snes/bios");
        string json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConsumerPlatformBiosDto>(json, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.SystemKey.ShouldBe("snes");
        body.Items.Count.ShouldBe(2);

        var availableItem = body.Items.Single(item => item.IsAvailable);
        availableItem.BiosId.ShouldBe(IdCoder.Encode(1));
        availableItem.Name.ShouldBe("[BIOS] Super Nintendo (USA)");
        availableItem.FileName.ShouldBe("snes-cpu.bin");
        availableItem.SizeBytes.ShouldBe((ByteCount)seed.ContentSizeBytes);
        availableItem.Sha1.ShouldBe(NewSha1(1).ToString());
        availableItem.Md5.ShouldBe(NewMd5(1).ToString());
        availableItem.Sha256.ShouldBe(seed.Sha256.ToString());
        availableItem.ContentGrant.ShouldNotBeNull();
        availableItem.ContentGrant.DownloadUrl.ShouldStartWith("/delivery/bios/");
        availableItem.ContentGrant.ExpiresAt.ShouldBeGreaterThan(fixture.TimeProvider.GetUtcNow());

        var unavailableItem = body.Items.Single(item => !item.IsAvailable);
        unavailableItem.BiosId.ShouldBe(IdCoder.Encode(1));
        unavailableItem.FileName.ShouldBe("snes-dsp.bin");
        unavailableItem.SizeBytes.ShouldBe((ByteCount)32);
        unavailableItem.Sha256.ShouldBeNull();
        unavailableItem.ContentGrant.ShouldBeNull();

        json.ShouldNotContain("\"fileId\"", Case.Insensitive);
        // Opaque grant tokens can contain the letters "cas" by chance. Assert
        // storage fields and paths, not arbitrary substrings inside signed data.
        json.ShouldNotContain("\"cas\"", Case.Insensitive);
        json.ShouldNotContain("/cas/", Case.Insensitive);
        json.ShouldNotContain("\"storagePath\"", Case.Insensitive);

        using var anonymousClient = fixture.Factory.CreateClient();
        var grantRouteResponse = await anonymousClient.GetAsync(availableItem.ContentGrant.DownloadUrl);
        byte[] content = await grantRouteResponse.Content.ReadAsByteArrayAsync();

        grantRouteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        grantRouteResponse.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        grantRouteResponse.Content.Headers.ContentLength.ShouldBe(seed.ContentSizeBytes);
        grantRouteResponse.Headers.CacheControl.ShouldNotBeNull();
        grantRouteResponse.Headers.CacheControl.Private.ShouldBeTrue();
        grantRouteResponse.Headers.CacheControl.NoStore.ShouldBeTrue();
        content.ShouldBe(seed.ContentBytes);
    }

    [Fact]
    public async Task GetPlatformBios_UnknownPlatform_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync("/api/systems/n64/bios");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlatformBios_PlatformNotExposedInLibrary_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);

        var response = await client.GetAsync("/api/systems/psx/bios");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlatformBios_InvalidLibraryConfiguration_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.InvalidLibraryId);

        var response = await client.GetAsync("/api/systems/snes/bios");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlatformBios_UserWithoutLibraryScope_ReturnsNotFound()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost");

        var response = await client.GetAsync("/api/systems/snes/bios");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlatformBios_UnauthenticatedRequest_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/api/systems/snes/bios");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RedeemBiosGrant_ExpiredToken_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        string downloadUrl = await IssueBiosGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var response = await client.GetAsync(downloadUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RedeemBiosGrant_TamperedToken_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        string downloadUrl = await IssueBiosGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(
            $"/delivery/bios/{ReplaceFirstSignatureCharacter(ExtractBiosGrantToken(downloadUrl))}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeliveryLanes_RejectCrossLaneTokens()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedBiosDataAsync(seed);
        string contentDownloadUrl = await IssueOwnedManifestGrantAsync(fixture, seed);
        string biosDownloadUrl = await IssueBiosGrantAsync(fixture, seed);
        using var client = fixture.Factory.CreateClient();

        var biosTokenOnContentRoute = await client.GetAsync(
            $"/delivery/content/{ExtractBiosGrantToken(biosDownloadUrl)}");
        var contentTokenOnBiosRoute = await client.GetAsync(
            $"/delivery/bios/{ExtractGrantToken(contentDownloadUrl)}");

        biosTokenOnContentRoute.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        contentTokenOnBiosRoute.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string ExtractGrantToken(string downloadUrl) =>
        downloadUrl["/delivery/content/".Length..];

    private static string ExtractBiosGrantToken(string downloadUrl) =>
        downloadUrl["/delivery/bios/".Length..];

    private static async Task<string> IssueBiosGrantAsync(
        ConsumerDeliveryFixture fixture,
        DeliverySeed seed)
    {
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        var response = await client.GetAsync("/api/systems/snes/bios");
        string json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConsumerPlatformBiosDto>(json, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        return body.Items.Single(item => item.IsAvailable).ContentGrant.ShouldNotBeNull().DownloadUrl;
    }

    private static async Task<string> IssueOwnedManifestGrantAsync(
        ConsumerDeliveryFixture fixture,
        DeliverySeed seed)
    {
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", seed.LibraryId);
        var response = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);
        string json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConsumerReleaseManifestDto>(json, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        return body.Items.ShouldHaveSingleItem().ContentGrant.ShouldNotBeNull().DownloadUrl;
    }

    private static string CreateGrantToken(
        DeliverySeed seed,
        TimeProvider timeProvider,
        string signingKeyId = "delivery-test",
        Sha256? sha256 = null,
        long? sizeBytes = null)
    {
        var issuer = new ConsumerContentGrantService(
            Options.Create(new ConsumerDeliveryOptions
            {
                SignedUrlTtlMinutes = 1,
                SigningKeyId = signingKeyId,
                SigningSecret = DeliverySigningSecret
            }),
            timeProvider);

        var grant = new ConsumerContentGrant(
            Guid.NewGuid(),
            seed.LibraryId,
            seed.OwnedTitleId,
            seed.OwnedReleaseId,
            seed.RomId,
            seed.FileId,
            sha256 ?? seed.Sha256,
            sizeBytes ?? seed.ContentSizeBytes);

        return ExtractGrantToken(issuer.IssueDownloadGrant(grant).DownloadUrl);
    }

    private static string ReplaceFirstSignatureCharacter(string token)
    {
        int signatureStart = token.IndexOf('.', StringComparison.Ordinal) + 1;
        signatureStart.ShouldBeGreaterThan(0);

        var chars = token.ToCharArray();
        chars[signatureStart] = chars[signatureStart] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    internal sealed record DeliverySeed(
        int LibraryId,
        int OtherLibraryId,
        int OwnedTitleId,
        int NonExposedTitleId,
        int PartialTitleId,
        int OtherLibraryTitleId,
        int ExposedUnownedTitleId,
        int OwnedReleaseId,
        int NonExposedReleaseId,
        int ExposedUnownedReleaseId,
        int PartialReleaseId,
        int OtherLibraryReleaseId,
        int InvalidLibraryId,
        int PlatformId,
        int RomId,
        int FileId,
        Sha256 Sha256,
        byte[] ContentBytes,
        long ContentSizeBytes);

    internal sealed class ConsumerDeliveryFixture : IAsyncDisposable
    {
        private readonly string _tempDataDirectory;
        private readonly PostgreSqlTestDatabase _database;

        private ConsumerDeliveryFixture(
            WebApplicationFactory<Romd.Consumer.Host.Program> factory,
            PostgreSqlTestDatabase database,
            string tempDataDirectory,
            ManualTimeProvider timeProvider)
        {
            Factory = factory;
            _database = database;
            _tempDataDirectory = tempDataDirectory;
            TimeProvider = timeProvider;
        }

        public WebApplicationFactory<Romd.Consumer.Host.Program> Factory { get; }

        public ManualTimeProvider TimeProvider { get; }

        public static async Task<ConsumerDeliveryFixture> CreateAsync()
        {
            var database = PostgreSqlTestDatabase.Create();
            var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);

            string tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-consumer-delivery-{Guid.NewGuid():N}");
            var webRoot = Path.Combine(tempDataDirectory, "wwwroot");
            Directory.CreateDirectory(webRoot);
            OpenIddictSigningKey.EnsureCreated(tempDataDirectory);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");

            var factory = new WebApplicationFactory<Romd.Consumer.Host.Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(AppContext.BaseDirectory);
                    builder.UseWebRoot(webRoot);
                    builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                    builder.UseSetting("Romd:DataDirectory", tempDataDirectory);
                    builder.UseSetting(
                        $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                        database.ConnectionString);
                    builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                    builder.UseSetting("Romd:JwtIssuer", TestJwtIssuer);
                    builder.UseSetting("Romd:ConsumerHost:JwtAudience", TestJwtAudience);
                    builder.UseSetting("Romd:ConsumerDelivery:SigningKeyId", "delivery-test");
                    builder.UseSetting("Romd:ConsumerDelivery:SigningSecret", DeliverySigningSecret);
                    builder.UseSetting("Romd:ConsumerDelivery:SignedUrlTtlMinutes", "1");

                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.AddHostedService<ServerInstanceIdentityInitializer>();
                        services.RemoveAll<DbContextOptions<RomdDbContext>>();
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton<TimeProvider>(timeProvider);

                        services.AddDbContext<RomdDbContext>((sp, options) =>
                        {
                            PostgreSqlConfiguration.Configure(options, database.ConnectionString);
                            options.UseOpenIddict();
                            options.EnableDetailedErrors();
                            options.AddInterceptors(
                                sp.GetRequiredService<AuditInterceptor>(),
                                new SearchDocumentInterceptor());
                        });

                        services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    });
                });

            var fixture = new ConsumerDeliveryFixture(factory, database, tempDataDirectory, timeProvider);
            await fixture.MigrateAsync();

            return fixture;
        }

        public HttpClient CreateAuthenticatedClient(Guid userId, string email, int? libraryId = null)
        {
            using var context = CreateSetupDbContext();
            if (!context.Users.Any(user => user.Id == userId))
            {
                string userName = $"consumer-{userId:N}";
                context.Users.Add(new RomdUser
                {
                    Id = userId,
                    UserName = userName,
                    NormalizedUserName = userName.ToUpperInvariant(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    LibraryId = libraryId,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                context.SaveChanges();
            }

            var client = Factory.CreateClient();
            client.WithTestUser(userId, email, [RomdRoleType.User], libraryId);

            return client;
        }

        public ConsumerValidatedContentGrant ValidateGrant(string token)
        {
            using var scope = Factory.Services.CreateScope();
            var issuer = scope.ServiceProvider.GetRequiredService<IConsumerContentGrantIssuer>();
            var result = issuer.ValidateDownloadGrant(token);

            result.IsError.ShouldBeFalse();
            return result.Value;
        }

        public ConsumerValidatedBiosGrant ValidateBiosGrant(string token)
        {
            using var scope = Factory.Services.CreateScope();
            var issuer = scope.ServiceProvider.GetRequiredService<IConsumerBiosGrantIssuer>();
            var result = issuer.ValidateBiosDownloadGrant(token);

            result.IsError.ShouldBeFalse();
            return result.Value;
        }

        public async Task<DeliverySeed> SeedDeliveryDataAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var contentStore = scope.ServiceProvider.GetRequiredService<IContentAddressableStore>();
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            var livingRoom = Library.CreateNew("Living Room", new LibraryConfiguration());
            var otherLibrary = Library.CreateNew("Other Room", new LibraryConfiguration());
            context.Libraries.AddRange(
                LibraryEntity.FromDomain(livingRoom),
                LibraryEntity.FromDomain(otherLibrary),
                new LibraryEntity
                {
                    Name = "Invalid Room",
                    ConfigurationJson = "{}",
                    ConfigurationState = LibraryConfigurationState.Invalid.ToString(),
                    ConfigurationError = "Invalid test configuration.",
                    NeedsMaterialization = false,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });
            await context.SaveChangesAsync();

            await context.Libraries
                .Where(library => library.Name == "Living Room" || library.Name == "Other Room")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(library => library.NeedsMaterialization, false));

            int libraryId = await context.Libraries
                .Where(library => library.Name == "Living Room")
                .Select(library => library.Id)
                .SingleAsync();
            int otherLibraryId = await context.Libraries
                .Where(library => library.Name == "Other Room")
                .Select(library => library.Id)
                .SingleAsync();
            int invalidLibraryId = await context.Libraries
                .Where(library => library.Name == "Invalid Room")
                .Select(library => library.Id)
                .SingleAsync();

            context.Platforms.Add(new PlatformEntity
            {
                Id = 1,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
                Manufacturer = "Nintendo",
                CreatedAt = now,
                CreatedByUserId = userId
            });

            byte[] romContent = [0x01, 0x02, 0x03, 0x04];
            await using var romStream = new MemoryStream(romContent);
            var romStoreResult = await contentStore.StoreAsync(romStream);

            context.Files.AddRange(
                NewFile(1, NewSha256(1), size: 1, now, userId),
                NewFile(2, romStoreResult.Key.Hash, romStoreResult.Size, now, userId));

            context.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
                },
                Id = 1,
                Name = "Consumer Delivery DAT",
                Description = "Consumer delivery test data",
                Type = "NoIntro",
                PlatformId = 1,
                OriginalFilename = "consumer-delivery.dat",
                FileId = 1,
                GameCount = 5,
                RomCount = 4,
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.Titles.AddRange(
                NewTitle(1, "Chrono Trigger", now, userId),
                NewTitle(2, "Mega Man", now, userId),
                NewTitle(3, "EarthBound", now, userId),
                NewTitle(4, "Partial Quest", now, userId),
                NewTitle(5, "Missing Quest", now, userId));

            context.SourceEntries.AddRange(
                NewSourceEntry(1, "Chrono Trigger (USA)", now, userId),
                NewSourceEntry(2, "Mega Man (USA)", now, userId),
                NewSourceEntry(3, "EarthBound (USA)", now, userId),
                NewSourceEntry(4, "Partial Quest (USA)", now, userId),
                NewSourceEntry(5, "Missing Quest (USA)", now, userId));

            context.DatGames.AddRange(
                NewDatGame(1, "Chrono Trigger (USA)", now, userId),
                NewDatGame(2, "Mega Man (USA)", now, userId),
                NewDatGame(3, "EarthBound (USA)", now, userId),
                NewDatGame(4, "Partial Quest (USA)", now, userId),
                NewDatGame(5, "Missing Quest (USA)", now, userId));

            context.RomFiles.AddRange(
                new RomFileEntity
                {
                    Id = 1,
                    OriginalFilename = "uploaded-name.sfc",
                    FileId = 2,
                    Sha1 = NewSha1(1),
                    Md5 = NewMd5(1),
                    Crc32 = Crc32.FromUInt32(0x01020304),
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new RomFileEntity
                {
                    Id = 2,
                    OriginalFilename = "earthbound-upload.sfc",
                    FileId = 2,
                    Sha1 = NewSha1(3),
                    Md5 = NewMd5(3),
                    Crc32 = Crc32.FromUInt32(0x03030303),
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            context.DatRoms.Add(new DatRomEntity
            {
                Id = 1,
                DatGameId = 1,
                Name = "chrono/chrono-trigger.sfc",
                Size = romStoreResult.Size,
                Crc = Crc32.FromUInt32(0x01020304),
                Md5 = NewMd5(1),
                Sha1 = NewSha1(1),
                RomFileId = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            });
            // Partial Quest is a multi-file release: part-1 shares the owned Chrono content hash (so
            // it resolves to the same stored file), while part-2/part-3 carry distinct unowned hashes.
            // The distinct hashes also keep this release's fingerprint separate from single-file Chrono.
            context.DatRoms.AddRange(
                new DatRomEntity
                {
                    Id = 7,
                    DatGameId = 3,
                    Name = "earthbound/earthbound.sfc",
                    Size = romStoreResult.Size,
                    Crc = Crc32.FromUInt32(0x03030303),
                    Md5 = NewMd5(3),
                    Sha1 = NewSha1(3),
                    RomFileId = 2,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 2,
                    DatGameId = 4,
                    Name = "partial/part-1.bin",
                    Size = romStoreResult.Size,
                    Sha1 = NewSha1(1),
                    RomFileId = 1,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 3,
                    DatGameId = 4,
                    Name = "partial/part-2.bin",
                    Size = 16,
                    Sha1 = NewSha1(42),
                    RomFileId = null,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 4,
                    DatGameId = 4,
                    Name = "partial/part-3.bin",
                    Size = 16,
                    Sha1 = NewSha1(43),
                    RomFileId = null,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            context.TitleSourceLinks.AddRange(
                NewLink(sourceEntryId: 1, titleId: 1, now, userId),
                NewLink(sourceEntryId: 2, titleId: 2, now, userId),
                NewLink(sourceEntryId: 3, titleId: 3, now, userId),
                NewLink(sourceEntryId: 4, titleId: 4, now, userId),
                NewLink(sourceEntryId: 5, titleId: 5, now, userId));

            await context.SaveChangesAsync();

            // Build the canonical catalog, then stamp each hand-crafted materialized release with its
            // stable CatalogReleaseId — the public release identity that manifests are addressed by.
            var releaseByGame = await TestHelpers.BuildCatalogAndMapReleasesAsync(context, 1);

            context.MaterializedLibraryReleases.AddRange(
                NewMaterializedRelease(libraryId, titleId: 1, datGameId: 1, releaseByGame[1], isOwned: true, isComplete: true),
                NewMaterializedRelease(
                    libraryId,
                    titleId: 2,
                    datGameId: 2,
                    releaseByGame[2],
                    isOwned: true,
                    isComplete: true,
                    isExposed: false),
                NewMaterializedRelease(libraryId, titleId: 4, datGameId: 4, releaseByGame[4], isOwned: true, isComplete: false),
                NewMaterializedRelease(libraryId, titleId: 5, datGameId: 5, releaseByGame[5], isOwned: false, isComplete: false),
                NewMaterializedRelease(otherLibraryId, titleId: 3, datGameId: 3, releaseByGame[3], isOwned: true, isComplete: true),
                NewMaterializedRelease(invalidLibraryId, titleId: 1, datGameId: 1, releaseByGame[1], isOwned: true, isComplete: true));

            await context.SaveChangesAsync();

            return new DeliverySeed(
                libraryId,
                otherLibraryId,
                OwnedTitleId: 1,
                NonExposedTitleId: 2,
                PartialTitleId: 4,
                OtherLibraryTitleId: 3,
                ExposedUnownedTitleId: 5,
                OwnedReleaseId: releaseByGame[1],
                NonExposedReleaseId: releaseByGame[2],
                ExposedUnownedReleaseId: releaseByGame[5],
                PartialReleaseId: releaseByGame[4],
                OtherLibraryReleaseId: releaseByGame[3],
                InvalidLibraryId: invalidLibraryId,
                PlatformId: 1,
                RomId: 1,
                FileId: 2,
                romStoreResult.Key.Hash,
                romContent,
                romStoreResult.Size);
        }

        public async Task<(int FirstCollectionId, int SecondCollectionId)> SeedCrossSurfaceDataAsync(
            DeliverySeed seed)
        {
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            context.MaterializedLibraryTitles.AddRange(
                NewMaterializedTitle(seed.LibraryId, seed.OwnedTitleId, seed.PlatformId),
                NewMaterializedTitle(seed.OtherLibraryId, seed.OtherLibraryTitleId, seed.PlatformId));

            var firstCollection = new CollectionEntity
            {
                Name = "Library A Picks",
                Description = "Titles visible only through Library A.",
                PlatformId = seed.PlatformId,
                IsSystem = false,
                SortOrder = 10,
                CreatedAt = now,
                CreatedByUserId = userId
            };
            var secondCollection = new CollectionEntity
            {
                Name = "Library B Picks",
                Description = "Titles visible only through Library B.",
                PlatformId = seed.PlatformId,
                IsSystem = false,
                SortOrder = 20,
                CreatedAt = now,
                CreatedByUserId = userId
            };
            context.Collections.AddRange(firstCollection, secondCollection);
            await context.SaveChangesAsync();

            context.LibraryCollections.AddRange(
                new LibraryCollectionEntity { LibraryId = seed.LibraryId, CollectionId = firstCollection.Id, IsFeatured = true },
                new LibraryCollectionEntity { LibraryId = seed.OtherLibraryId, CollectionId = secondCollection.Id, IsFeatured = true });
            context.CollectionItems.AddRange(
                NewCollectionItem(firstCollection.Id, seed.OwnedTitleId, now, userId),
                NewCollectionItem(secondCollection.Id, seed.OtherLibraryTitleId, now, userId));
            await context.SaveChangesAsync();
            await SeedBiosDataAsync(seed);

            return (firstCollection.Id, secondCollection.Id);
        }

        public async Task ReassignUserAsync(Guid userId, int libraryId)
        {
            await using var context = CreateSetupDbContext();
            int updated = await context.Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.LibraryId, libraryId));
            updated.ShouldBe(1);
        }

        public async Task CorruptLibraryConfigurationAsync(
            int libraryId,
            string configurationJson,
            string configurationState)
        {
            await using var context = CreateSetupDbContext();
            int updated = await context.Libraries
                .Where(library => library.Id == libraryId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(library => library.ConfigurationJson, configurationJson)
                    .SetProperty(library => library.ConfigurationState, configurationState));
            updated.ShouldBe(1);
        }

        public async Task<int> OrphanReleaseProjectionAsync(int libraryId, int releaseId)
        {
            const int orphanReleaseId = 9_000_001;
            await using var context = CreateSetupDbContext();
            int updated = await context.MaterializedLibraryReleases
                .Where(release =>
                    release.LibraryId == libraryId &&
                    release.CatalogReleaseId == releaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    release => release.CatalogReleaseId,
                    orphanReleaseId));
            updated.ShouldBe(1);
            return orphanReleaseId;
        }

        public async Task MismatchReleaseProjectionTitleAsync(
            int libraryId,
            int releaseId,
            int mismatchedTitleId)
        {
            await using var context = CreateSetupDbContext();
            int updated = await context.MaterializedLibraryReleases
                .Where(release =>
                    release.LibraryId == libraryId &&
                    release.CatalogReleaseId == releaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    release => release.TitleId,
                    mismatchedTitleId));
            updated.ShouldBe(1);
        }

        public async Task MismatchReleaseProjectionPlatformAsync(int libraryId, int releaseId)
        {
            await using var context = CreateSetupDbContext();
            int updated = await context.MaterializedLibraryReleases
                .Where(release =>
                    release.LibraryId == libraryId &&
                    release.CatalogReleaseId == releaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    release => release.PlatformId,
                    release => release.PlatformId + 1000));
            updated.ShouldBe(1);
        }

        public async Task DuplicateReleaseProjectionAsync(int libraryId, int releaseId)
        {
            await using var context = CreateSetupDbContext();
            var existing = await context.MaterializedLibraryReleases
                .AsNoTracking()
                .SingleAsync(release =>
                    release.LibraryId == libraryId &&
                    release.CatalogReleaseId == releaseId);
            int datGameId = await context.MaterializedLibraryReleases
                .MaxAsync(release => release.DatGameId) + 1000;
            context.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = existing.LibraryId,
                TitleId = existing.TitleId,
                CatalogReleaseId = existing.CatalogReleaseId,
                DatGameId = datGameId,
                DatFileId = existing.DatFileId,
                PlatformId = existing.PlatformId,
                IsEligible = existing.IsEligible,
                IsComplete = existing.IsComplete,
                IsOwned = existing.IsOwned,
                IsPlayable = existing.IsPlayable,
                IsBlocked = existing.IsBlocked,
                BlockReason = existing.BlockReason,
                IsExposed = existing.IsExposed,
                ExposureReason = existing.ExposureReason
            });
            await context.SaveChangesAsync();
        }

        public async Task<int> CreateLibraryAsync(string name, bool needsMaterialization)
        {
            await using var context = CreateSetupDbContext();
            var entity = LibraryEntity.FromDomain(Library.CreateNew(name, new LibraryConfiguration()));
            entity.NeedsMaterialization = needsMaterialization;
            context.Libraries.Add(entity);
            await context.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<(RomdUser User, string AccessToken)> IssueDeviceAccessTokenAsync(
            string userName,
            string email,
            string password,
            int libraryId)
        {
            await SeedOpenIddictAsync();
            RomdUser user;
            await using (var context = CreateSetupDbContext())
            {
                user = RomdUser.Create(userName, email);
                user.NormalizedUserName = userName.ToUpperInvariant();
                user.NormalizedEmail = email.ToUpperInvariant();
                user.SecurityStamp = Guid.NewGuid().ToString();
                user.ConcurrencyStamp = Guid.NewGuid().ToString();
                user.LibraryId = libraryId;
                user.PasswordHash = new PasswordHasher<RomdUser>().HashPassword(user, password);
                context.Users.Add(user);
                await context.SaveChangesAsync();
            }

            using var client = Factory.CreateClient();
            var deviceResponse = await client.PostAsync(
                "/connect/device",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
                    ["scope"] = "offline_access email profile roles"
                }));
            var device = await ReadJsonObjectAsync(deviceResponse);
            deviceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            string deviceCode = RequiredString(device, "device_code");
            string userCode = RequiredString(device, "user_code");

            var approvalResponse = await client.PostAsync(
                "/connect/verify",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["user_code"] = userCode,
                    ["login"] = userName,
                    ["password"] = password
                }));
            approvalResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var tokenResponse = await client.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
                    ["device_code"] = deviceCode
                }));
            var token = await ReadJsonObjectAsync(tokenResponse);
            tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            return (user, RequiredString(token, "access_token"));
        }

        private async Task SeedOpenIddictAsync()
        {
            var services = new ServiceCollection();
            services.AddDbContext<RomdDbContext>(options =>
            {
                options.UseNpgsql(_database.ConnectionString);
                options.UseOpenIddict();
            });
            services.AddOpenIddict()
                .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<RomdDbContext>());

            await using var provider = services.BuildServiceProvider();
            var seeder = new RomdOpenIddictApplicationSeeder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().Build());
            await seeder.StartAsync(CancellationToken.None);
        }

        private static async Task<Dictionary<string, JsonElement>> ReadJsonObjectAsync(
            HttpResponseMessage response)
        {
            string json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? [];
        }

        private static string RequiredString(
            IReadOnlyDictionary<string, JsonElement> values,
            string name) =>
            values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new InvalidOperationException($"Response did not include '{name}'.");

        /// <summary>
        ///     Adds BIOS data on top of <see cref="SeedDeliveryDataAsync" />: one snes BIOS group with
        ///     an owned file (reusing the seeded CAS content) and an unowned file, plus a psx platform
        ///     with no materialized releases so the exposure gate has a deny case.
        /// </summary>
        public async Task SeedBiosDataAsync(DeliverySeed seed)
        {
            await using var context = CreateSetupDbContext();
            var now = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();

            context.Platforms.Add(new PlatformEntity
            {
                Id = 2,
                Name = "PlayStation",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "PlayStation", BaseCompactLabel = "PlayStation", CanonicalKey = "psx", ShortName = "psx",
                Manufacturer = "Sony",
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.SourceEntries.Add(NewSourceEntry(6, "[BIOS] Super Nintendo (USA)", now, userId));
            context.DatGames.Add(new DatGameEntity
            {
                Id = 6,
                DatFileId = 1,
                SourceEntryId = 6,
                Name = "[BIOS] Super Nintendo (USA)",
                IsBios = true,
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.Bios.Add(new BiosEntity
            {
                Id = 1,
                PlatformId = seed.PlatformId,
                Name = "[BIOS] Super Nintendo (USA)",
                NormalizedName = "[bios] super nintendo (usa)",
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.BiosGameMappings.Add(new BiosGameMappingEntity
            {
                DatGameId = 6,
                BiosId = 1,
                CreatedAt = now,
                CreatedByUserId = userId
            });

            context.DatRoms.AddRange(
                new DatRomEntity
                {
                    Id = 5,
                    DatGameId = 6,
                    Name = "snes-cpu.bin",
                    Size = seed.ContentSizeBytes,
                    Md5 = NewMd5(1),
                    Sha1 = NewSha1(1),
                    RomFileId = seed.RomId,
                    CreatedAt = now,
                    CreatedByUserId = userId
                },
                new DatRomEntity
                {
                    Id = 6,
                    DatGameId = 6,
                    Name = "snes-dsp.bin",
                    Size = 32,
                    Sha1 = NewSha1(77),
                    RomFileId = null,
                    CreatedAt = now,
                    CreatedByUserId = userId
                });

            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await _database.DisposeAsync();

            if (Directory.Exists(_tempDataDirectory))
            {
                Directory.Delete(_tempDataDirectory, true);
            }
        }

        private async Task MigrateAsync()
        {
            await using var context = CreateSetupDbContext();
            await context.Database.MigrateAsync();
        }

        private RomdDbContext CreateSetupDbContext()
        {
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(new SearchDocumentInterceptor())
                .UseOpenIddict()
                .EnableDetailedErrors()
                .Options;

            return new RomdDbContext(options);
        }
    }

    private static FileEntityPersistence NewFile(
        int id,
        Sha256 sha256,
        long size,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            Sha256 = sha256,
            Size = size,
            SizeOnDisk = size,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static TitleEntity NewTitle(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            PlatformId = 1,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "Completed",
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static DatGameEntity NewDatGame(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            DatFileId = 1,
            SourceEntryId = id,
            Name = name,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static SourceEntryEntity NewSourceEntry(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            CatalogSourceId = 1,
            EntryKey = name,
            Name = name,
            PlatformId = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static TitleSourceLinkEntity NewLink(int sourceEntryId, int titleId, DateTimeOffset now, Guid userId) =>
        new()
        {
            SourceEntryId = sourceEntryId,
            TitleId = titleId,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static MaterializedLibraryReleaseEntity NewMaterializedRelease(
        int libraryId,
        int titleId,
        int datGameId,
        int catalogReleaseId,
        bool isOwned,
        bool isComplete,
        bool isExposed = true) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            CatalogReleaseId = catalogReleaseId,
            DatGameId = datGameId,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = isExposed,
            IsComplete = isComplete,
            IsOwned = isOwned,
            IsPlayable = isExposed && isOwned && isComplete,
            IsBlocked = !isExposed,
            BlockReason = isExposed ? null : "ExcludedDat",
            IsExposed = isExposed,
            ExposureReason = isExposed ? "ExposedDefault" : "ExcludedDat"
        };

    private static MaterializedLibraryTitleEntity NewMaterializedTitle(
        int libraryId,
        int titleId,
        int platformId) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            PlatformId = platformId,
            IsVisible = true,
            IsOwned = true,
            IsPlayable = true,
            EligibleReleaseCount = 1,
            PlayableReleaseCount = 1,
            ExposedReleaseCount = 1,
            Availability = LibraryTitleAvailability.Playable.ToString()
        };

    private static CollectionItemEntity NewCollectionItem(
        int collectionId,
        int titleId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            CollectionId = collectionId,
            TitleId = titleId,
            SortOrder = 10,
            AddedAt = now,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static Sha256 NewSha256(byte firstByte) => HashWithFirstByte<Sha256>(Sha256.ByteLength, firstByte);

    private static Sha1 NewSha1(byte firstByte) => HashWithFirstByte<Sha1>(Sha1.ByteLength, firstByte);

    private static Md5 NewMd5(byte firstByte) => HashWithFirstByte<Md5>(Md5.ByteLength, firstByte);

    private static T HashWithFirstByte<T>(int byteLength, byte firstByte)
        where T : struct, IHashValue<T>
    {
        var bytes = new byte[byteLength];
        bytes[0] = firstByte;
        return T.FromSpan(bytes);
    }

    public sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan timeSpan) => _utcNow = _utcNow.Add(timeSpan);
    }
}
