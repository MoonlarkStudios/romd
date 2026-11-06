using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Ids;
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
        var entries = (await reader.GetFromJsonAsync<Page<SourceEntryReference>>($"/api/dats/{id}/source-entries?limit=1"))!;
        var entry = entries.Items.ShouldHaveSingleItem();
        entries.HasNextPage.ShouldBeFalse();
        entries.NextCursor.ShouldBeNull();
        entry.TitleId.ShouldNotBeNull();
        entry.ActiveSources.ShouldBeGreaterThan(0);
        var filtered = await TestHelpers.ReadAllPageItemsAsync<SourceEntryReference>(reader,
            $"/api/dats/{id}/source-entries?titleId={entry.TitleId}&query=Lifecycle");
        filtered.Single().EntryId.ShouldBe(entry.EntryId);
        (await TestHelpers.ReadAllPageItemsAsync<TitleSourceReference>(reader, $"/api/titles/{entry.TitleId}/source-references"))
            .ShouldContain(r => r.DatId == id && r.SystemKey == platform && r.HasActiveDefinition);
        foreach (var action in new[] { "Disabled", "Discontinued", "Active", "Delete" })
        {
            var preview = (await client.GetFromJsonAsync<SourceRemovalImpact>($"/api/dats/{id}/source-impact?action={action}"))!;
            preview.Busy.ShouldBeFalse();
            preview.Versions.ShouldBe(1);
            using (var stale = await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action, reviewToken = "stale" }))
            using (await ProblemEnvelope.ReadAsync(stale, 409, "SourceLifecycle.Stale"))
            { }
            using var applied = await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action, preview.ReviewToken });
            applied.StatusCode.ShouldBe(HttpStatusCode.NoContent, await applied.Content.ReadAsStringAsync());
            (await applied.Content.ReadAsStringAsync()).ShouldBeEmpty();
            applied.Headers.ETag.ShouldBeNull();
            if (action != "Delete")
            {
                var retained = (await TestHelpers.ReadAllPageItemsAsync<SourceEntryReference>(client,
                    $"/api/dats/{id}/source-entries")).Single();
                retained.TitleId.ShouldBe(entry.TitleId);
                if (action is "Disabled" or "Discontinued")
                    retained.ActiveSources.ShouldBe(0);
            }
        }
        (await client.GetAsync($"/api/dats/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SourceLifecycle_MissingSourceAndInvalidAction_PreserveExistingErrorOrder()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var id = IdCoder.Encode(int.MaxValue);
        using (var preview = await client.GetAsync($"/api/dats/{id}/source-impact?action=Delete"))
        using (await ProblemEnvelope.ReadAsync(preview, 404, "SourceLifecycle.NotFound"))
        { }
        using (var missing = await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action = "Delete", reviewToken = "stale" }))
        using (await ProblemEnvelope.ReadAsync(missing, 404, "SourceLifecycle.NotFound"))
        { }
        using (var invalid = await client.PostAsJsonAsync($"/api/dats/{id}/source-lifecycle", new { action = "delete", reviewToken = "stale" }))
        using (await ProblemEnvelope.ReadAsync(invalid, 400, "SourceLifecycle.Action"))
        { }
    }
}
