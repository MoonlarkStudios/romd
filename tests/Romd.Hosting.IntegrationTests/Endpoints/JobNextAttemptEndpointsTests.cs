using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class JobNextAttemptEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetJob_DeferredAndUndeferred_AlwaysSerializesNextAttempt()
    {
        // The fixture's dispatch worker claims due pending jobs and clears their next attempt, so
        // the deferred job is due in the future and the undeferred one is already terminal.
        var due = new DateTimeOffset(2099, 9, 22, 14, 32, 0, TimeSpan.Zero);
        var deferred = UploadJobEntity.FromDomain(UploadJob.Create("deferred.zip", createdAt: DateTimeOffset.UtcNow));
        deferred.NextAttemptAtUtc = due;
        var finished = UploadJob.Create("undeferred.zip", createdAt: DateTimeOffset.UtcNow);
        finished.Fail("Finished before the read.", failedAt: DateTimeOffset.UtcNow);
        var undeferred = UploadJobEntity.FromDomain(finished);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AddRange(deferred, undeferred);
            await db.SaveChangesAsync();
        }
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);

        using var scheduled = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{deferred.Id}"));
        using var unscheduled = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{undeferred.Id}"));

        scheduled.RootElement.GetProperty("nextAttemptAt").GetDateTimeOffset().ShouldBe(due);
        unscheduled.RootElement.TryGetProperty("nextAttemptAt", out var absent).ShouldBeTrue();
        absent.ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
