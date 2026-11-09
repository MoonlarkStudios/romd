using System.Net;
using System.Net.Http.Json;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ReferenceCatalogEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task UploadAsset_RasterUpload_ReturnsHashLocationAndAnonymousImmutableAsset()
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 4, 3, 2, 1];
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        using var client = fixture.CreateAuthenticatedClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        using var response = await client.PostAsync("/api/assets", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe("/api/assets/" + hash);
        (await response.Content.ReadFromJsonAsync<string>()).ShouldBe(hash);
        using var anonymous = fixture.CreateClient();
        using var read = await anonymous.GetAsync("/api/assets/" + hash);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        read.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        read.Headers.ETag!.Tag.ShouldBe($"\"{hash}\"");
        read.Headers.CacheControl!.Public.ShouldBeTrue();
        read.Headers.CacheControl.ToString().ShouldContain("immutable");
        read.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        read.Headers.GetValues("Content-Security-Policy").ShouldBe(["default-src 'none'; sandbox"]);
        using var denied = await anonymous.PostAsync("/api/assets", new ByteArrayContent(bytes));
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var reader = fixture.CreateAuthenticatedClient();
        reader.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        reader.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "User");
        using var forbidden = await reader.PostAsync("/api/assets", new ByteArrayContent(bytes));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UploadAsset_UnreviewedSvg_ReturnsExistingValidationEnvelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var content = new ByteArrayContent("<svg><script/></svg>"u8.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");

        using var response = await client.PostAsync("/api/assets", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "ReferenceCatalog.Invalid");
        (await response.Content.ReadAsStringAsync()).ShouldContain("Use a PNG or WebP icon.");
    }

    [Fact]
    public async Task UploadAsset_OverTwoMiB_ReturnsPayloadTooLargeEnvelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        const string marker = "reference-upload-private-payload";
        var bytes = new byte[2 * 1024 * 1024 + 1];
        System.Text.Encoding.UTF8.GetBytes(marker).CopyTo(bytes, 0);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await client.PostAsync("/api/assets", content);
        using var body = await ProblemEnvelope.ReadAsync(response, 413, "Request.PayloadTooLarge");
        body.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();
        body.RootElement.GetProperty("detail").GetString().ShouldBe("The request body exceeds the allowed size.");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(marker);
    }

    [Fact]
    public async Task Catalog_RequiresAuthentication_AndSupportsConditionalCurrentSnapshot()
    {
        using var anonymous = fixture.CreateClient();
        (await anonymous.GetAsync("/api/catalog-snapshot")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/catalog-snapshot");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var descriptor = (await response.Content.ReadFromJsonAsync<ReferenceCatalogDescriptor>())!;
        var catalog = await client.ReadCatalogAsync();
        response.Headers.ETag!.Tag.ShouldBe($"\"{descriptor.Revision}\"");
        descriptor.Revision.ShouldNotBe(descriptor.CatalogRevision);
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
        (await anonymous.PostAsJsonAsync("/api/systems", new CreateSystem("unauthorized", "Denied", CompactLabel: "NO"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_NewIdentity_IsImmediatelyVisibleWithoutChangingContract()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var key = "local-future-" + Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/systems", new CreateSystem(key, "Future Console", CompactLabel: "FUT"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe("/api/systems/" + key);
        var catalog = (await client.ReadCatalogAsync());
        catalog.Systems.Single(x => x.Key == key).CompactLabel.ShouldBe("FUT");
        (await client.ReadCatalogAsync()).Revision.ShouldBe(catalog.Revision);
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
        var company = await client.PostAsJsonAsync("/api/companies", new CreateCompany(key, "Maker"));
        company.StatusCode.ShouldBe(HttpStatusCode.Created);
        var systemKey = "local-system-" + Guid.NewGuid().ToString("N");
        var system = await client.PostAsJsonAsync("/api/systems", new CreateSystem(systemKey, "Machine", "MCH", ManufacturerKeys: [key]));
        system.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var rename = new HttpRequestMessage(HttpMethod.Patch, "/api/companies/" + key)
        {
            Content = JsonContent.Create(new CompanyPatch { Name = "New maker" })
        };
        rename.Headers.IfMatch.Add(company.Headers.ETag!);
        var renamed = await client.SendAsync(rename);
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await client.GetAsync("/api/systems/" + systemKey);
        var facts = (await read.Content.ReadFromJsonAsync<SystemResource>())!;
        facts.Manufacturers.ShouldHaveSingleItem().Name.ShouldBe("New maker");
        var snapshot = (await client.ReadCatalogAsync());
        snapshot.Systems.Single(x => x.Key == systemKey).Manufacturers.ShouldBe(facts.Manufacturers);
        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/api/systems/" + systemKey)
        {
            Content = JsonContent.Create(new SystemPatch { Name = "Stale" })
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
    public async Task PublishedMutation_InvalidatesOldPin_AndPagedRowRevisionSupportsConditionalWrite()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var old = (await client.GetFromJsonAsync<ReferenceCatalogDescriptor>("/api/catalog-snapshot"))!;
        var key = "local-paged-" + Guid.NewGuid().ToString("N");
        using var created = await client.PostAsJsonAsync("/api/companies", new CreateCompany(key, "Paged company"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var page = (await client.GetFromJsonAsync<Page<CompanyResource>>("/api/companies?query=" + key + "&limit=1"))!;
        var row = page.Items.ShouldHaveSingleItem();
        row.Revision.ShouldBe(created.Headers.ETag!.Tag.Trim('"'));
        using var edit = new HttpRequestMessage(HttpMethod.Patch, "/api/companies/" + key) { Content = JsonContent.Create(new CompanyPatch { Name = "Changed from page" }) };
        edit.Headers.TryAddWithoutValidation("If-Match", "\"" + row.Revision + "\"");
        using var changed = await client.SendAsync(edit);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<CompanyResource>())!.Revision.ShouldBe(changed.Headers.ETag!.Tag.Trim('"'));
        using var stalePin = await client.GetAsync("/api/catalog-snapshot/companies?revision=" + old.CatalogRevision);
        using var staleProblem = await ProblemEnvelope.ReadAsync(stalePin, 409, "ReferenceCatalog.SnapshotChanged");
        var current = (await client.GetFromJsonAsync<ReferenceCatalogDescriptor>("/api/catalog-snapshot"))!;
        current.CatalogRevision.ShouldNotBe(old.CatalogRevision);
        current.ContentHash.ShouldNotBe(old.ContentHash);
        current.Revision.ShouldNotBe(old.Revision);
        using var catalogValidator = new HttpRequestMessage(HttpMethod.Get, "/api/catalog-snapshot");
        catalogValidator.Headers.TryAddWithoutValidation("If-None-Match", "\"" + current.CatalogRevision + "\"");
        using var fresh = await client.SendAsync(catalogValidator);
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/companies/" + key);
        delete.Headers.IfMatch.Add(changed.Headers.ETag!);
        (await client.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
        var created = await client.PostAsJsonAsync("/api/systems", new CreateSystem(key, "Custom System", CompactLabel: "CS"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var path = "/api/systems/" + key;
        (await client.PatchAsJsonAsync(path, new SystemPatch { Name = "Changed" })).StatusCode.ShouldBe((HttpStatusCode)428);
        using var edit = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new SystemPatch { Name = "Changed", Icon = null }) };
        edit.Headers.IfMatch.Add(created.Headers.ETag!);
        var updated = await client.SendAsync(edit);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resource = (await updated.Content.ReadFromJsonAsync<SystemResource>())!;
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
