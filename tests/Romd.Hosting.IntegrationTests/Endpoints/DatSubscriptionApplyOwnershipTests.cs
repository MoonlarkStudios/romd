using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Identity;
using Romd.Domain.Source.Dat;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class DatSubscriptionApplyOwnershipTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_InitialOrReplacement_ReturnsCanonicalDurableAcceptanceAndReplaysWhileDisabled(bool replacement)
    {
        var signed = new CatalogClient();
        await using var host = new AcceptanceFixture(signed);
        await host.InitializeAsync();
        int? installedId = replacement ? await InstallAsync(host, signed.Original) : null;
        using var client = host.CreateAuthenticatedClient();
        var subscription = await EnrollAsync(client, signed, installedId);
        var preview = (await client.GetFromJsonAsync<Models.DatReplacementPreview>($"/api/dat-subscriptions/{subscription.Id}/preview")).ShouldNotBeNull();

        using var accepted = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, preview.CandidateSha256 });

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await accepted.Content.ReadFromJsonAsync<Models.JobReference>()).ShouldNotBeNull();
        reference.JobId.ShouldNotBe(Guid.Empty);
        accepted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe($"/api/jobs/{reference.JobId}");
        accepted.Headers.ETag.ShouldBeNull();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var row = await db.DatSubscriptions.SingleAsync(item => item.CatalogId == signed.CatalogId);
        row.JobId.ShouldBe(reference.JobId);
        row.State.ShouldBe("Applying");
        row.Message.ShouldBeNull();
        var job = await db.Jobs.SingleAsync(item => item.Id == reference.JobId);
        job.JobType.ShouldBe(replacement ? "replace_dat" : "upload");
        job.SourceFilename.ShouldBe(replacement ? "reviewed-catalog.dat" : "subscribed-catalog.dat");
        job.CreatedByUserId.ShouldBe(replacement ? null : Guid.Parse(client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserIdHeader).Single()));
        job.PlatformId.ShouldBe(replacement ? null : (await db.Platforms.SingleAsync(platform => platform.ShortName == "psx")).Id);
        var dispatch = await db.JobDispatches.SingleAsync(item => item.JobId == reference.JobId);
        dispatch.JobType.ShouldBe(job.JobType);
        (await File.ReadAllBytesAsync(Path.Combine(host.DataDirectoryPath, "temp", "jobs", $"{reference.JobId:N}", job.SourceFilename))).ShouldBe(signed.Candidate);
        if (replacement)
            (await db.Jobs.OfType<ReplaceDatJobEntity>().SingleAsync(item => item.Id == reference.JobId)).ExistingDatId.ShouldBe(installedId.GetValueOrDefault());
        int fetches = signed.Fetches;
        signed.Enabled = false;
        using var replay = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, preview.CandidateSha256 });
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await replay.Content.ReadFromJsonAsync<Models.JobReference>()).ShouldNotBeNull().JobId.ShouldBe(reference.JobId);
        signed.Fetches.ShouldBe(fetches);
        (await db.Jobs.CountAsync(item => item.Id == reference.JobId)).ShouldBe(1);
        using var stale = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, CandidateSha256 = preview.CandidateSha256.ToUpperInvariant() });
        using var problem = await ProblemEnvelope.ReadAsync(stale, 409, "DatEnrollment.Stale");
    }

    [Fact]
    public async Task Apply_CatalogHeldWhileDisabled_ReturnsBusyBeforeStaleAndUnavailable()
    {
        var signed = new CatalogClient();
        await using var host = new AcceptanceFixture(signed);
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        var subscription = await EnrollAsync(client, signed, null);
        var preview = (await client.GetFromJsonAsync<Models.DatReplacementPreview>($"/api/dat-subscriptions/{subscription.Id}/preview")).ShouldNotBeNull();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(146, hashtext({signed.CatalogId}))");
        signed.Enabled = false;

        using var busy = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, CandidateSha256 = new string('0', 64) });

        using var busyProblem = await ProblemEnvelope.ReadAsync(busy, 409, "DatEnrollment.Busy");
        await transaction.RollbackAsync();
        using var stale = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, CandidateSha256 = new string('0', 64) });
        using var staleProblem = await ProblemEnvelope.ReadAsync(stale, 409, "DatEnrollment.Stale");
        using var unavailable = await client.PostAsJsonAsync($"/api/dat-subscriptions/{subscription.Id}/apply",
            new { preview.ActiveSha256, preview.CandidateSha256 });
        using var unavailableProblem = await ProblemEnvelope.ReadAsync(unavailable, 409, "DatEnrollment.Unavailable");
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.JobDispatches.CountAsync()).ShouldBe(0);
        (await db.DatSubscriptions.SingleAsync()).State.ShouldBe("ReadyToImport");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("bad", "bad")]
    [InlineData("", "bad")]
    public async Task Apply_InvalidPins_ReturnsFingerprintFieldEnvelopeBeforeCommand(string? active, string? candidate)
    {
        var handler = Substitute.For<ICommandHandler<ApplyDatSubscriptionCommand, Guid>>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<ApplyDatSubscriptionCommand, Guid>>();
            services.AddSingleton(handler);
        }));
        using var client = Authenticated(factory.CreateClient());
        string id = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();

        using var rejected = await client.PostAsJsonAsync($"/api/dat-subscriptions/{id}/apply",
            new { ActiveSha256 = active, CandidateSha256 = candidate });

        using var problem = await ProblemEnvelope.ReadAsync(rejected, 400, "DatReview.InvalidFingerprint");
        var expected = active == "" ? new[] { "candidateSha256" } : new[] { "activeSha256", "candidateSha256" };
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order().ShouldBe(expected);
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_AnonymousOrManager_RejectsBeforeCommand(bool authenticated)
    {
        var handler = Substitute.For<ICommandHandler<ApplyDatSubscriptionCommand, Guid>>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<ApplyDatSubscriptionCommand, Guid>>();
            services.AddSingleton(handler);
        }));
        using var client = factory.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);
        string id = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();

        using var response = await client.PostAsJsonAsync($"/api/dat-subscriptions/{id}/apply", new { ActiveSha256 = "", CandidateSha256 = new string('0', 64) });

        response.StatusCode.ShouldBe(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_UnknownSubscription_ReturnsNotFoundProblem()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string id = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();
        using var response = await client.PostAsJsonAsync($"/api/dat-subscriptions/{id}/apply", new { ActiveSha256 = "", CandidateSha256 = new string('0', 64) });
        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "DatEnrollment.NotFound");
    }

    private HttpClient Authenticated(HttpClient client)
    {
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();
        return client;
    }
    private static async Task<Models.DatCatalogSubscription> EnrollAsync(HttpClient client, CatalogClient signed, int? installedId)
    {
        using var response = await client.PostAsJsonAsync("/api/dat-subscriptions", new
        { signed.CatalogId, DatId = installedId is int id ? new Romd.Application.Common.Ids.Sqid(id).ToString() : null });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Models.DatCatalogSubscription>()).ShouldNotBeNull();
    }
    private static async Task<int> InstallAsync(IntegrationTestFixture host, byte[] bytes)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        using var stream = new MemoryStream(bytes, writable: false);
        await using var staged = await storage.StageAsync(stream);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var publication = await storage.PublishStagedAsync(staged);
        if (publication.RequiresFlush)
            await work.FlushAsync();
        var file = publication.ReadAfterFlush();
        int platform = await db.Platforms.Where(row => row.ShortName == "psx").Select(row => row.Id).SingleAsync();
        await new DatRepository(db, TimeProvider.System).AddStagedAsync(
            DatFile.CreateNew("Apply owner catalog", "Catalog", DatType.Redump, "installed.dat", file.File.Id, DateTimeOffset.UnixEpoch, platform),
            DatSource.CreateNew(DateTimeOffset.UnixEpoch));
        await transaction.CommitAsync();
        return await db.DatFiles.Select(row => row.Id).SingleAsync();
    }
    private sealed class CatalogClient : ISignedDatCatalogClient
    {
        public string CatalogId { get; } = $"test/psx/apply-{Guid.NewGuid():N}";
        public byte[] Original { get; } = Encoding.UTF8.GetBytes(Document("Installed"));
        public byte[] Candidate { get; } = Encoding.UTF8.GetBytes(Document("Reviewed"));
        public bool Enabled { get; set; } = true;
        public int Fetches { get; private set; }
        private static string Document(string game) => $"<datafile><header><name>Apply owner catalog</name></header><game name='{game}'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
        public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<IReadOnlyList<PublishedDatCatalog>>>(new[] { new PublishedDatCatalog(CatalogId, "psx", "Apply owner catalog", "test", "healthy", Convert.ToHexStringLower(SHA256.HashData(Candidate)), 1, 1, null) });
        public Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct)
        { Fetches++; return Task.FromResult<ErrorOr<byte[]>>(Candidate); }
    }
    private sealed class AcceptanceFixture(CatalogClient signed) : IntegrationTestFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISignedDatCatalogClient>();
                services.AddSingleton<ISignedDatCatalogClient>(signed);
            });
        }
    }
}
