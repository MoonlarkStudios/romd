using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Realtime;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Infrastructure.Realtime;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class LibraryCreatePersistenceTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task LibraryTrackingScope_CreateUpdateAndRead_PreserveExplicitOptIn()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var jsonOptions = fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        string name = $"Tracking scope {Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync("/api/libraries", new CreateLibraryRequest(name,
            new LibraryConfiguration { IncludeUntrackedTitles = true }), jsonOptions);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var library = (await created.Content.ReadFromJsonAsync<Library>(jsonOptions))!;
        library.Configuration.ShouldNotBeNull();
        library.Configuration.IncludeUntrackedTitles.ShouldBeTrue();
        var updated = await client.PatchAsJsonAsync($"/api/libraries/{library.Id}", new UpdateLibraryRequest(name,
            new LibraryConfiguration { IncludeUntrackedTitles = false }), jsonOptions);
        updated.EnsureSuccessStatusCode();
        var persisted = await client.GetFromJsonAsync<Library>($"/api/libraries/{library.Id}", jsonOptions);
        persisted.ShouldNotBeNull();
        persisted.Configuration.ShouldNotBeNull();
        persisted.Configuration.IncludeUntrackedTitles.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateLibrary_HandlerStageConfigurationError_KeysTheEnvelopeErrorsByCode()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var jsonOptions = fixture.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        // A well-formed id of no title passes the endpoint and fails the handler's reference check.
        using var response = await client.PostAsJsonAsync("/api/libraries", new CreateLibraryRequest(
            $"Missing title {Guid.NewGuid():N}",
            new LibraryConfiguration { IncludeTitleIds = [IdCoder.Encode(int.MaxValue)] }), jsonOptions);

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Libraries.InvalidConfiguration");
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["Libraries.InvalidConfiguration"]);
    }

    [Fact]
    public async Task CreateLibrary_GeneratedIdMatchesLocationPersistedRowAndAtomicEvent()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string name = $"Generated ID {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            "/api/libraries",
            new CreateLibraryRequest(name));
        var jsonOptions = fixture.Services
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .SerializerOptions;
        var body = await response.Content.ReadFromJsonAsync<Library>(jsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        body.ShouldNotBeNull();
        body.Configuration.ShouldNotBeNull();
        body.Configuration.IncludeUntrackedTitles.ShouldBeFalse();
        response.Headers.Location?.OriginalString.ShouldBe($"/api/libraries/{body.Id}");
        IdCoder.TryDecode(body.Id, out int libraryId).ShouldBeTrue();

        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var persisted = await context.Libraries
            .AsNoTracking()
            .SingleAsync(row => row.Name == name);
        persisted.Id.ShouldBe(libraryId);

        var payloads = (await context.AdminRealtimeOutboxEvents
                .AsNoTracking()
                .Where(row => row.EventType == AdminRealtimeEventTypes.LibraryUpdated)
                .Select(row => row.PayloadJson)
                .ToListAsync())
            .Select(AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedPayload>)
            .Where(payload => payload.Name == name)
            .ToList();
        var payload = payloads.ShouldHaveSingleItem();
        payload.LibraryId.ShouldBe(IdCoder.Encode(libraryId));
        payload.NeedsMaterialization.ShouldBeTrue();
    }
}
