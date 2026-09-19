using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Romd.Admin.Application.Titles;
using Romd.Domain.Identity;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Models;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class UploadArchiveOnlyEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("/api/upload")]
    [InlineData("/api/upload/rom")]
    public async Task Upload_TrackedOnly_RequiresTrackingAndPersistsChoice(string route)
    {
        var titles = Substitute.For<ITitleRepository>();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITitleRepository>();
            services.AddSingleton(titles);
        }));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var userId = await db.Users.Select(u => u.Id).FirstAsync();
        using var client = factory.CreateClient().WithTestUser(userId, "admin@localhost", [RomdRoleType.Admin]);
        using var blocked = new MultipartFormDataContent();
        blocked.Add(new ByteArrayContent([1, 2, 3]), "file", "tracked.rom");
        using var rejection = await client.PostAsync(route + "?trackedOnly=true", blocked);
        rejection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await rejection.Content.ReadAsStringAsync()).ShouldContain("Track at least one title");

        titles.HasTrackedAsync(null, Arg.Any<CancellationToken>()).Returns(true);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "tracked.rom");
        using var response = await client.PostAsync(route + "?trackedOnly=true", content);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = (await response.Content.ReadFromJsonAsync<UploadAccepted>())!;
        var persisted = await db.Set<UploadJobEntity>().SingleAsync(job => job.Id == accepted.JobId);
        persisted.TrackedOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task GenericUpload_RepeatedIdentity_ReturnsOneDurableJobAndBatch()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var requestId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent([1, 2, 3]), "file", "repeat.rom");
            using var response = await client.PostAsync($"/api/upload?requestId={requestId}&batchId={batchId}", content);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadFromJsonAsync<UploadAccepted>())!.JobId.ShouldBe(requestId);
        }
        var jobs = await client.GetFromJsonAsync<List<UploadJobDto>>($"/api/upload/batches/{batchId}");
        jobs.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(requestId);
        using var different = new MultipartFormDataContent();
        different.Add(new ByteArrayContent([1, 2, 3]), "file", "different.rom");
        using var conflict = await client.PostAsync($"/api/upload?requestId={requestId}&batchId={batchId}", different);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    public async Task GenericUpload_ArchiveOnlyOption_PersistsOnUploadJob(
        bool? requestedArchiveOnly,
        bool expectedArchiveOnly)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "archive-choice.rom");
        string url = requestedArchiveOnly.HasValue
            ? $"/api/upload?archiveOnly={requestedArchiveOnly.Value.ToString().ToLowerInvariant()}"
            : "/api/upload";

        using var response = await client.PostAsync(url, content);
        var accepted = await response.Content.ReadFromJsonAsync<UploadAccepted>();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.ShouldNotBeNull();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var persisted = await db.Set<UploadJobEntity>()
            .AsNoTracking()
            .SingleAsync(job => job.Id == accepted.JobId);
        persisted.ArchiveOnly.ShouldBe(expectedArchiveOnly);
    }
}
