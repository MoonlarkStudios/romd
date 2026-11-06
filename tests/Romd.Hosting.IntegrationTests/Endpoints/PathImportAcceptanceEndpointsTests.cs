using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Import;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class PathImportAcceptanceEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_ManagerCopyOrMove_ReturnsAcceptedCanonicalJobAndAtomicallyPersistsPreparedInputsAndDispatch(bool move)
    {
        await using var host = new AcceptanceFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(TestAuthHandler.RolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, nameof(RomdRoleType.Manager));
        string source = CreateSource(host);

        using var response = await client.PostAsJsonAsync("/api/upload/from-path",
            new { path = source, systemKey = "psx", move, maxParallelRoms = 6, allowUnidentified = true, archiveOnly = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        reference.JobId.ShouldNotBe(Guid.Empty);
        response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe($"/api/jobs/{reference.JobId}");
        response.Headers.ETag.ShouldBeNull();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = await db.Jobs.OfType<UploadJobEntity>().SingleAsync(row => row.Id == reference.JobId);
        job.SourceFilename.ShouldBe(Path.GetFileName(source) + "/");
        job.ImportSourcePath.ShouldBe(source);
        job.ImportMove.ShouldBe(move);
        job.CreatedByUserId.ShouldBe(Guid.Parse(client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserIdHeader).Single()));
        job.PlatformId.ShouldBe((await db.Platforms.SingleAsync(platform => platform.ShortName == "psx")).Id);
        job.MaxParallelRoms.ShouldBe(6);
        job.AllowUnidentified.ShouldBeTrue();
        job.ArchiveOnly.ShouldBeTrue();
        job.TrackedOnly.ShouldBeFalse();
        job.Phase.ShouldBe("Pending");
        var dispatch = await db.JobDispatches.SingleAsync(row => row.JobId == reference.JobId);
        dispatch.JobType.ShouldBe("upload");
        (await File.ReadAllBytesAsync(Path.Combine(host.DataDirectoryPath, "temp", "jobs", $"{reference.JobId:N}", "nested", "game.rom")))
            .ShouldBe(new byte[] { 11, 22, 33 });
        (await File.ReadAllBytesAsync(Path.Combine(source, "nested", "game.rom"))).ShouldBe(new byte[] { 11, 22, 33 });
        File.Exists(ImportManifest.PathFor(host.DataDirectoryPath, reference.JobId)).ShouldBe(move);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Import_PathConcurrencyOutsideGenericUploadRange_PreservesAcceptanceAndPersistedValue(int maxParallel)
    {
        await using var host = new AcceptanceFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        string source = CreateSource(host);

        using var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = source, maxParallelRoms = maxParallel });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = await db.Jobs.OfType<UploadJobEntity>().SingleAsync(row => row.Id == reference.JobId);
        job.MaxParallelRoms.ShouldBe(maxParallel);
        job.PlatformId.ShouldBeNull();
        job.AllowUnidentified.ShouldBeFalse();
        job.ArchiveOnly.ShouldBeFalse();
        job.TrackedOnly.ShouldBeFalse();
        job.ImportMove.ShouldBeFalse();
        (await db.JobDispatches.SingleAsync(row => row.JobId == reference.JobId)).JobType.ShouldBe("upload");
        File.Exists(ImportManifest.PathFor(host.DataDirectoryPath, reference.JobId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Import_NoTrackedTitles_RejectsBeforeBodySystemResolutionAndPathInspection()
    {
        var tracked = Substitute.For<ITrackedTitleReader>();
        tracked.HasTrackedAsync(Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>()).Returns(false);
        var reference = Substitute.For<IReferenceCatalogService>();
        var preparation = Substitute.For<IPathImportPreparation>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITrackedTitleReader>();
            services.RemoveAll<IReferenceCatalogService>();
            services.RemoveAll<IPathImportPreparation>();
            services.AddSingleton(tracked);
            services.AddSingleton(reference);
            services.AddSingleton(preparation);
        }));
        using var client = Authenticated(factory.CreateClient());

        using var response = await client.PostAsJsonAsync("/api/upload/from-path",
            new { path = "relative path must not be inspected", systemKey = "unknown", trackedOnly = true, maxParallelRoms = 0 });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Upload.NoTrackedTitles");
        problem.RootElement.GetProperty("errors").GetProperty("trackedOnly").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["Track at least one title before importing tracked-title ROMs."]);
        await tracked.Received(1).HasTrackedAsync(null, Arg.Any<CancellationToken>());
        await reference.DidNotReceive().ResolveSystemIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        preparation.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no-such-system")]
    public async Task Import_UnknownBodySystem_ReturnsExactFieldEnvelopeBeforeInspectingPath(string systemKey)
    {
        var preparation = Substitute.For<IPathImportPreparation>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPathImportPreparation>();
            services.AddSingleton(preparation);
        }));
        using var client = Authenticated(factory.CreateClient());

        using var response = await client.PostAsJsonAsync("/api/upload/from-path",
            new { path = "relative path must not be inspected", systemKey });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Systems.UnknownKey");
        var errors = problem.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(field => field.Name).ShouldBe(["systemKey"]);
        errors.GetProperty("systemKey").EnumerateArray().Select(value => value.GetString()).ShouldBe(["Not a known system key."]);
        preparation.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_AnonymousOrContributor_RejectsBeforeCommand(bool authenticated)
    {
        var handler = Substitute.For<ICommandHandler<ImportFromPathCommand, Guid>>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<ImportFromPathCommand, Guid>>();
            services.AddSingleton(handler);
        }));
        using var client = factory.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), "contributor@localhost", [RomdRoleType.Contributor]);

        using var response = await client.PostAsJsonAsync("/api/upload/from-path", new { path = "must not be inspected" });

        response.StatusCode.ShouldBe(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    private HttpClient Authenticated(HttpClient client)
    {
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();
        return client;
    }

    private static string CreateSource(IntegrationTestFixture host)
    {
        string source = Path.Combine(host.ImportRootPath, $"path-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllBytes(Path.Combine(source, "nested", "game.rom"), [11, 22, 33]);
        return source;
    }

    private sealed class AcceptanceFixture : IntegrationTestFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // This fixture owns an independent database; retain the accepted work for observation.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
