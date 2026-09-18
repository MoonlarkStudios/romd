using System.Net;
using System.Net.Http.Json;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ReferenceCatalogEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Catalog_RequiresAuthentication_AndSupportsConditionalCurrentSnapshot()
    {
        using var anonymous = fixture.CreateClient();
        (await anonymous.GetAsync("/api/catalog-snapshot")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/catalog-snapshot");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalog = (await response.Content.ReadFromJsonAsync<ReferenceCatalogDto>())!;
        response.Headers.ETag!.Tag.ShouldBe($"\"{catalog.Revision}\"");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog-snapshot");
        request.Headers.IfNoneMatch.Add(response.Headers.ETag);
        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        var snapshot = await client.GetAsync($"/api/catalog-snapshots/{catalog.Revision}");
        snapshot.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var asset = catalog.Systems.First(x => x.Icon != null).Icon!;
        var artwork = await anonymous.GetAsync(asset.Url);
        artwork.StatusCode.ShouldBe(HttpStatusCode.OK);
        artwork.Headers.ETag!.Tag.ShouldBe($"\"{asset.Sha256}\"");
        artwork.Headers.CacheControl!.Public.ShouldBeTrue();
        (await anonymous.PostAsJsonAsync("/api/systems", new CreateSystemDto("unauthorized", "Denied", CompactLabel: "NO"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_NewIdentity_IsImmediatelyVisibleWithoutChangingContract()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var key = "local-future-" + Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/systems", new CreateSystemDto(key, "Future Console", CompactLabel: "FUT"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe("/api/systems/" + key);
        var catalog = (await client.GetFromJsonAsync<ReferenceCatalogDto>("/api/catalog-snapshot"))!;
        catalog.Systems.Single(x => x.Key == key).CompactLabel.ShouldBe("FUT");
        (await client.GetFromJsonAsync<ReferenceCatalogDto>("/api/catalog-snapshot"))!.Revision.ShouldBe(catalog.Revision);
    }
    [Theory]
    [InlineData("/api/companies", "{\"key\":\"local-invalid-company\",\"name\":\"Invalid\",\"compactLabel\":\"NO\"}")]
    [InlineData("/api/systems", "{\"key\":\"local-invalid-system\",\"name\":\"Invalid\",\"compactLabel\":\"NO\",\"minimumAge\":12}")]
    public async Task Creation_RejectsFieldsFromOtherReferenceTypes(string path, string json)
    {
        using var client = fixture.CreateAuthenticatedClient();
        (await client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CompanyEndpoints_EnforceConditionalWrites_AndPublishDependentSystemFacts()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var key = "local-maker-" + Guid.NewGuid().ToString("N");
        var company = await client.PostAsJsonAsync("/api/companies", new CreateCompanyDto(key, "Maker"));
        company.StatusCode.ShouldBe(HttpStatusCode.Created);
        var systemKey = "local-system-" + Guid.NewGuid().ToString("N");
        var system = await client.PostAsJsonAsync("/api/systems", new CreateSystemDto(systemKey, "Machine", "MCH", ManufacturerKeys: [key]));
        system.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var rename = new HttpRequestMessage(HttpMethod.Patch, "/api/companies/" + key)
        {
            Content = JsonContent.Create(new CompanyPatchDto { Name = "New maker" })
        };
        rename.Headers.IfMatch.Add(company.Headers.ETag!);
        var renamed = await client.SendAsync(rename);
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await client.GetAsync("/api/systems/" + systemKey);
        var facts = (await read.Content.ReadFromJsonAsync<SystemResourceDto>())!;
        facts.Manufacturers.ShouldHaveSingleItem().Name.ShouldBe("New maker");
        var snapshot = (await client.GetFromJsonAsync<ReferenceCatalogDto>("/api/catalog-snapshot"))!;
        snapshot.Systems.Single(x => x.Key == systemKey).Manufacturers.ShouldBe(facts.Manufacturers);
        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/api/systems/" + systemKey)
        {
            Content = JsonContent.Create(new SystemPatchDto { Name = "Stale" })
        };
        stale.Headers.IfMatch.Add(system.Headers.ETag!);
        (await client.SendAsync(stale)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var dependent = new HttpRequestMessage(HttpMethod.Delete, "/api/companies/" + key);
        dependent.Headers.IfMatch.Add(renamed.Headers.ETag!);
        (await client.SendAsync(dependent)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var deleteSystem = new HttpRequestMessage(HttpMethod.Delete, "/api/systems/" + systemKey);
        deleteSystem.Headers.IfMatch.Add(read.Headers.ETag!);
        (await client.SendAsync(deleteSystem)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteCompany = new HttpRequestMessage(HttpMethod.Delete, "/api/companies/" + key);
        deleteCompany.Headers.IfMatch.Add(renamed.Headers.ETag!);
        (await client.SendAsync(deleteCompany)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Create_InvalidKey_ReturnsValidationInsteadOfConstructingALocation()
    {
        using var client = fixture.CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/systems", new { key = (string?)null, name = "Invalid" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resource_ConditionalPatch_ResetAndDeletion_UseHttpSemantics()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var key = "local-edit-" + Guid.NewGuid().ToString("N");
        var created = await client.PostAsJsonAsync("/api/systems", new CreateSystemDto(key, "Custom System", CompactLabel: "CS"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var path = "/api/systems/" + key;
        (await client.PatchAsJsonAsync(path, new SystemPatchDto { Name = "Changed" })).StatusCode.ShouldBe((HttpStatusCode)428);
        using var edit = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new SystemPatchDto { Name = "Changed", Icon = null }) };
        edit.Headers.IfMatch.Add(created.Headers.ETag!);
        var updated = await client.SendAsync(edit);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resource = (await updated.Content.ReadFromJsonAsync<SystemResourceDto>())!;
        resource.Name.ShouldBe("Changed");
        resource.CompactLabel.ShouldBe("CS");
        using var stale = new HttpRequestMessage(HttpMethod.Delete, path);
        stale.Headers.IfMatch.Add(created.Headers.ETag!);
        (await client.SendAsync(stale)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var deletion = new HttpRequestMessage(HttpMethod.Delete, path);
        deletion.Headers.IfMatch.Add(updated.Headers.ETag!);
        (await client.SendAsync(deletion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/rating-boards/Esrb/ratings/E")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
