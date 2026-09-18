using System.Net;
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
