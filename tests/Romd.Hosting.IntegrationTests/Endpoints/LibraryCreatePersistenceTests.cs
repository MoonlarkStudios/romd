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
        var body = await response.Content.ReadFromJsonAsync<LibraryDto>(jsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        body.ShouldNotBeNull();
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
