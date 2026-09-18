using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Contracts.Management.Models;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SourceLifecycleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ReviewedLifecycle_EnforcesAuthorization_ListsLinkedEntries_DisablesRestoresAndDeletes()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var platform = await TestHelpers.GetFirstSystemKeyAsync(client);
        var name = $"Lifecycle HTTP {Guid.NewGuid():N}";
        using var upload = TestHelpers.CreateMultipartContent($"<datafile><header><name>{name}</name></header><game name='{name} Game'><rom name='lifecycle.rom' size='1' crc='87654321'/></game></datafile>", "lifecycle.dat");
        var accepted = await client.PostAsync($"/api/upload/dat?systemKey={platform}", upload);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        await TestHelpers.WaitForJobCompletionAsync(client, job.RootElement.GetProperty("jobId").GetString()!);
        var id = await TestHelpers.GetDatIdByNameAsync(client, name);
        using var anonymous = fixture.CreateClient();
        (await anonymous.GetAsync($"/api/dats/{id}/source-impact?action=Delete")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action = "Delete", reviewToken = "x" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var reader = fixture.CreateAuthenticatedClient();
        reader.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        reader.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "User");
        (await reader.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action = "Delete", reviewToken = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var entries = (await reader.GetFromJsonAsync<SourceEntryPage>($"/api/dats/{id}/source-entries?limit=1"))!;
        var entry = entries.Items.ShouldHaveSingleItem();
        entry.TitleId.ShouldNotBeNull(); entry.ActiveSources.ShouldBeGreaterThan(0);
        var filtered = (await reader.GetFromJsonAsync<SourceEntryPage>($"/api/dats/{id}/source-entries?titleId={entry.TitleId}&search=Lifecycle"))!;
        filtered.Items.Single().EntryId.ShouldBe(entry.EntryId);
        (await reader.GetFromJsonAsync<List<TitleSourceReference>>($"/api/titles/{entry.TitleId}/source-references", new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }))!
            .ShouldContain(r => r.DatId == id && r.SystemKey == platform && r.HasActiveDefinition);
        foreach (var action in new[] { "Disabled", "Active", "Delete" })
        {
            var preview = (await client.GetFromJsonAsync<SourceRemovalImpact>($"/api/dats/{id}/source-impact?action={action}"))!;
            preview.Busy.ShouldBeFalse(); preview.Versions.ShouldBe(1);
            (await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action, reviewToken = "stale" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var applied = await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action, preview.ReviewToken });
            applied.StatusCode.ShouldBe(HttpStatusCode.NoContent, await applied.Content.ReadAsStringAsync());
            if (action != "Delete")
            {
                var retained = (await client.GetFromJsonAsync<SourceEntryPage>($"/api/dats/{id}/source-entries"))!.Items.Single();
                retained.TitleId.ShouldBe(entry.TitleId);
                if (action == "Disabled") retained.ActiveSources.ShouldBe(0);
            }
        }
        (await client.GetAsync($"/api/dats/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
