using System.Net;
using System.Net.Http.Json;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Collections;
using Romd.Contracts.Consumer.Delivery;
using Romd.Contracts.Consumer.Libraries;
using Romd.Contracts.Consumer.Releases;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerLiveLibraryAssignmentEndpointsTests
{
    [Fact]
    public async Task SameToken_AfterLiveLibraryReassignment_UsesNewLibraryAcrossEveryConsumerRead()
    {
        await using var fixture =
            await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var collections = await fixture.SeedCrossSurfaceDataAsync(seed);
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(
            userId,
            "same-token@localhost",
            seed.LibraryId);

        var before = await client.GetFromJsonAsync<LibraryContextDto>("/api/me/library");
        before.ShouldNotBeNull();
        before.Name.ShouldBe("Living Room");

        await fixture.ReassignUserAsync(userId, seed.OtherLibraryId);

        var libraryResponse = await client.GetAsync("/api/me/library");
        var library = await libraryResponse.Content.ReadFromJsonAsync<LibraryContextDto>();
        libraryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        library.ShouldNotBeNull();
        library.Name.ShouldBe("Other Room");
        library.Counts.OwnedTitleCount.ShouldBe(1);
        library.FeaturedCollections.Select(item => item.Name).ShouldBe(["Library B Picks"]);

        var platformsResponse = await client.GetAsync("/api/me/library/systems");
        var platforms = await platformsResponse.Content
            .ReadFromJsonAsync<Page<ConsumerPlatformSummaryDto>>();
        platformsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        platforms.ShouldNotBeNull();
        var platform = platforms.Items.ShouldHaveSingleItem();
        platform.TitleCount.ShouldBe(1);

        var platformDetailResponse = await client.GetAsync($"/api/me/library/systems/{platform.Key}");
        var platformDetail = await platformDetailResponse.Content
            .ReadFromJsonAsync<ConsumerPlatformDetailDto>();
        platformDetailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        platformDetail.ShouldNotBeNull();
        platformDetail.TitleCount.ShouldBe(1);

        var catalogResponse = await client.GetAsync("/api/catalog?limit=10");
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<Page<ConsumerTitleCardDto>>();
        catalogResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        catalog.ShouldNotBeNull();
        catalog.Items.Select(item => item.Name).ShouldBe(["EarthBound"]);
        catalog.Items.ShouldNotContain(item => item.Name == "Chrono Trigger");

        var matchingSearch = await client.GetFromJsonAsync<Page<ConsumerTitleCardDto>>(
            "/api/catalog?query=earth&limit=10");
        matchingSearch.ShouldNotBeNull();
        matchingSearch.Items.Select(item => item.Name).ShouldBe(["EarthBound"]);
        var formerSearch = await client.GetFromJsonAsync<Page<ConsumerTitleCardDto>>(
            "/api/catalog?query=chrono&limit=10");
        formerSearch.ShouldNotBeNull();
        formerSearch.Items.ShouldBeEmpty();

        var newTitleResponse = await client.GetAsync(
            $"/api/titles/{IdCoder.Encode(seed.OtherLibraryTitleId)}");
        var newTitle = await newTitleResponse.Content.ReadFromJsonAsync<ConsumerTitleDetailDto>();
        newTitleResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        newTitle.ShouldNotBeNull();
        newTitle.Name.ShouldBe("EarthBound");

        var formerTitleResponse = await client.GetAsync(
            $"/api/titles/{IdCoder.Encode(seed.OwnedTitleId)}");
        formerTitleResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var collectionsResponse = await client.GetAsync("/api/collections");
        var visibleCollections = await collectionsResponse.Content
            .ReadFromJsonAsync<IReadOnlyList<ConsumerCollectionDto>>();
        collectionsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        visibleCollections.ShouldNotBeNull();
        visibleCollections.Select(item => item.Name).ShouldBe(["Library B Picks"]);

        var collectionDetailResponse = await client.GetAsync(
            $"/api/collections/{IdCoder.Encode(collections.SecondCollectionId)}");
        var collectionDetail = await collectionDetailResponse.Content
            .ReadFromJsonAsync<ConsumerCollectionDto>();
        collectionDetailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        collectionDetail.ShouldNotBeNull();
        collectionDetail.Name.ShouldBe("Library B Picks");

        var collectionTitlesResponse = await client.GetAsync(
            $"/api/collections/{IdCoder.Encode(collections.SecondCollectionId)}/titles");
        var collectionTitles = await collectionTitlesResponse.Content
            .ReadFromJsonAsync<Page<ConsumerCollectionTitleDto>>();
        collectionTitlesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        collectionTitles.ShouldNotBeNull();
        collectionTitles.Items.Select(item => item.Name).ShouldBe(["EarthBound"]);

        var formerCollectionResponse = await client.GetAsync(
            $"/api/collections/{IdCoder.Encode(collections.FirstCollectionId)}");
        formerCollectionResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var biosResponse = await client.GetAsync("/api/systems/snes/bios");
        var bios = await biosResponse.Content.ReadFromJsonAsync<ConsumerPlatformBiosDto>();
        biosResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        bios.ShouldNotBeNull();
        var biosGrantUrl = bios.Items.Single(item => item.IsAvailable)
            .ContentGrant.ShouldNotBeNull()
            .DownloadUrl;
        var validatedBiosGrant = fixture.ValidateBiosGrant(
            biosGrantUrl["/delivery/bios/".Length..]);
        validatedBiosGrant.Grant.UserId.ShouldBe(userId);
        validatedBiosGrant.Grant.LibraryId.ShouldBe(seed.OtherLibraryId);

        var manifestResponse = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OtherLibraryReleaseId)}/manifest",
            content: null);
        var manifest = await manifestResponse.Content
            .ReadFromJsonAsync<ConsumerReleaseManifestDto>();
        manifestResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        manifest.ShouldNotBeNull();
        manifest.TitleId.ShouldBe(IdCoder.Encode(seed.OtherLibraryTitleId));
        var contentGrantUrl = manifest.Items.ShouldHaveSingleItem()
            .ContentGrant.ShouldNotBeNull()
            .DownloadUrl;
        var validatedContentGrant = fixture.ValidateGrant(
            contentGrantUrl["/delivery/content/".Length..]);
        validatedContentGrant.Grant.UserId.ShouldBe(userId);
        validatedContentGrant.Grant.LibraryId.ShouldBe(seed.OtherLibraryId);

        var formerManifestResponse = await client.PostAsync(
            $"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest",
            content: null);
        formerManifestResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
