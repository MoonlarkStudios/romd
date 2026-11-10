using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Releases;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class DiscSetDeliveryEndpointsTests
{
    [Theory]
    [InlineData("original", false, "psx")]
    [InlineData("chd", false, "psx")]
    [InlineData("original", true, "psx")]
    [InlineData("original", false, "saturn")]
    [InlineData("chd", true, "saturn")]
    public async Task DownloadTicket_CompleteAuthorizedSet_StreamsExactOriginalsAndOrderedPlaylist(string format, bool attached, string systemKey)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        using (var db = fixture.CreateSetupDbContext())
            await db.Platforms.Where(row => row.Id == 1).ExecuteUpdateAsync(row => row.SetProperty(platform => platform.CanonicalKey, systemKey)
                .SetProperty(platform => platform.ShortName, systemKey));
        var (id, _, expected) = await SeedDiscSet(fixture, seed);
        if (attached)
            await SeedOriginalLayouts(fixture);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "disc-download@test", seed.LibraryId);
        string root = $"/api/disc-sets/{IdCoder.Encode(id)}";
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatus>(root + "/status"))!;
        status.SystemKey.ShouldBe(systemKey);
        var request = new ConsumerDiscSetSelectionRequest(1, status.Revision, status.ObservationId, status.Discs.Select(x => x.ReleaseId).ToArray(), false);
        using var created = await client.PostAsJsonAsync(root + "/download-tickets?format=" + format, request);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var ticket = (await created.Content.ReadFromJsonAsync<ContentGrant>())!;
        created.Headers.Location!.ToString().ShouldBe(ticket.DownloadUrl);
        using var anonymous = fixture.Factory.CreateClient();
        // The bounded ticket retains its exact selection after a later policy change.
        using (var db = fixture.CreateSetupDbContext())
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 102).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
        using var download = await anonymous.GetAsync(ticket.DownloadUrl);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        download.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync()));
        zip.Entries.Select(x => x.FullName).Order().ShouldBe(expected.Keys.Append("Disc Game (USA).m3u").Order());
        foreach (var (path, bytes) in expected)
        {
            await using var input = zip.GetEntry(path)!.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            output.ToArray().ShouldBe(bytes);
        }
        using var playlist = new StreamReader(zip.GetEntry("Disc Game (USA).m3u")!.Open(), Encoding.UTF8);
        (await playlist.ReadToEndAsync()).ShouldBe("disc-01/game.cue\ndisc-02/game.cue\n");
        (await client.PostAsJsonAsync(root + "/download-tickets", request)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.PostAsJsonAsync(root + "/download-tickets", request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        string token = ticket.DownloadUrl.Split('/')[^1];
        (await anonymous.GetAsync("/delivery/content/" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/delivery/disc-set/" + new string('0', 64))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DownloadTicket_PartialOrStaleSelection_RequiresExplicitConsentAndCurrentObservation()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, _, _) = await SeedDiscSet(fixture, seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "partial-download@test", seed.LibraryId);
        string root = $"/api/disc-sets/{IdCoder.Encode(id)}";
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatus>(root + "/status"))!;
        var request = new ConsumerDiscSetSelectionRequest(1, status.Revision, status.ObservationId, [status.Discs[1].ReleaseId], false);
        (await client.PostAsJsonAsync(root + "/download-tickets", request)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync(root + "/download-tickets?format=rvz", request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(root + "/download-tickets", request with { AllowPartial = true, Revision = Guid.NewGuid() })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync(root + "/download-tickets", request with { AllowPartial = true, ObservationId = "stale" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var created = await client.PostAsJsonAsync(root + "/download-tickets", request with { AllowPartial = true });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var ticket = (await created.Content.ReadFromJsonAsync<ContentGrant>())!;
        using var response = await client.GetAsync(ticket.DownloadUrl);
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldEndWith("-partial.zip");
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        zip.Entries.Count.ShouldBe(4);
        using var cue = new StreamReader(zip.GetEntry("disc-01/game.cue")!.Open());
        (await cue.ReadToEndAsync()).ShouldContain("REM disc 2");
    }
}
