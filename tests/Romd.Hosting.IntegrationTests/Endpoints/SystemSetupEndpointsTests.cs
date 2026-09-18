using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Contracts.Management.Models;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SystemSetupEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task StopSubscription_UnknownIdentityReturnsNotFound()
    {
        var id = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();
        using var client = fixture.CreateAuthenticatedClient();
        (await client.DeleteAsync($"/api/dat-subscriptions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Directory_EnableDisable_PreservesPlatformAndAliases()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var systems = await client.GetFromJsonAsync<ManagedSystemDto[]>("/api/system-setup");
        var psx = systems!.Single(x => x.Key == "psx");
        (await client.PutAsJsonAsync($"/api/system-setup/{psx.Key}/enabled", new { enabled = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/api/system-setup/{psx.Key}/enabled", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var hidden = (await client.GetFromJsonAsync<ManagedSystemDto[]>("/api/system-setup"))!.Single(x => x.Key == psx.Key);
        hidden.Enabled.ShouldBeFalse(); hidden.Aliases.ShouldBe(psx.Aliases);
        (await client.GetAsync($"/api/systems/{psx.Key}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManualPreview_IsReadOnly_AndChangedDocumentCannotBeApproved()
    {
        using var client = fixture.CreateAuthenticatedClient();
        const string document = "<datafile><header><name>PS1</name></header><game name='Setup Test'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
        using var form = TestHelpers.CreateMultipartContent(document, "catalog.dat");
        var response = await client.PostAsync("/api/system-setup/dat-preview", form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<SystemDatPreviewDto>())!;
        preview.SuggestedSystemKey.ShouldNotBeNull(); preview.Preview.CandidateEntries.ShouldBe(1);
        using var changed = TestHelpers.CreateMultipartContent(document.Replace("size='1'", "size='2'"), "changed.dat");
        var rejected = await client.PostAsync($"/api/system-setup/{preview.SuggestedSystemKey}/import?sha256={preview.Preview.CandidateSha256}", changed);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("errorCode").GetString().ShouldBe("SystemSetup.Stale");
    }

    [Fact]
    public async Task MalformedDatAndInvalidPlatform_ReturnValidationErrors()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var invalid = TestHelpers.CreateMultipartContent("<datafile><header>", "broken.dat");
        (await client.PostAsync("/api/system-setup/dat-preview", invalid)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync("/api/system-setup/not-a-sqid/enabled", new { enabled = true })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
