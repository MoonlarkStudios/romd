using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
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
    [InlineData("/api/upload", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api/upload/dat", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api/upload/rom", false, HttpStatusCode.Unauthorized)]
    [InlineData("/api/upload", true, HttpStatusCode.Forbidden)]
    [InlineData("/api/upload/dat", true, HttpStatusCode.Forbidden)]
    [InlineData("/api/upload/rom", true, HttpStatusCode.Forbidden)]
    public async Task Upload_RequiresContributorBeforeInputValidation(string route, bool authenticated, HttpStatusCode expected)
    {
        using var client = fixture.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        using var form = new MultipartFormDataContent();

        using var response = await client.PostAsync(route, form);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/api/upload")]
    [InlineData("/api/upload/rom")]
    public async Task Upload_TrackedOnly_RequiresTrackingAndPersistsChoice(string route)
    {
        var titles = Substitute.For<ITrackedTitleReader>();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITrackedTitleReader>();
            services.AddSingleton(titles);
        }));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var userId = await db.Users.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
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
        var accepted = (await response.Content.ReadFromJsonAsync<JobReference>())!;
        var persisted = await db.Set<UploadJobEntity>().SingleAsync(job => job.Id == accepted.JobId);
        persisted.TrackedOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_InsufficientStorage_Answers409Envelope()
    {
        var preparation = Substitute.For<IUploadWorkspacePreparation>();
        preparation.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Arg.Any<Stream>(), Arg.Any<IJobWorkspaceLease>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<IPreparedJobWorkspace>>(UploadErrors.InsufficientStorage()));
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUploadWorkspacePreparation>();
            services.AddSingleton(preparation);
        }));
        await using var scope = fixture.Services.CreateAsyncScope();
        var userId = await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Users.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        using var client = factory.CreateClient().WithTestUser(userId, "admin@localhost", [RomdRoleType.Admin]);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "large.rom");

        using var response = await client.PostAsync("/api/upload", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 409, "Upload.InsufficientStorage");
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
            (await response.Content.ReadFromJsonAsync<JobReference>())!.JobId.ShouldBe(requestId);
        }
        var jobs = await client.GetFromJsonAsync<Romd.Contracts.Common.Models.Page<Job>>($"/api/jobs?correlationId={batchId}&jobType=upload&archive=all&limit=100");
        jobs.ShouldNotBeNull().Items.ShouldHaveSingleItem().Id.ShouldBe(requestId);
        jobs.HasNextPage.ShouldBeFalse();
        using var different = new MultipartFormDataContent();
        different.Add(new ByteArrayContent([1, 2, 3]), "file", "different.rom");
        using var conflict = await client.PostAsync($"/api/upload?requestId={requestId}&batchId={batchId}", different);
        using var body = await ProblemEnvelope.ReadAsync(conflict, 409, "Upload.RequestIdReused");
        body.RootElement.GetProperty("detail").GetString()
            .ShouldBe("This upload request ID has already been used for another import.");
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
        var accepted = await response.Content.ReadFromJsonAsync<JobReference>();

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
