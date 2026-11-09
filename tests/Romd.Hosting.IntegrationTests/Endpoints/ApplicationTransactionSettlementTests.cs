using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Common.Persistence;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

using ContractJob = Romd.Contracts.Management.Models.Job;

using UploadJob = Romd.Domain.Jobs.UploadJob;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed partial class ApplicationTransactionSettlementTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Archive_ActualMappedOwner_PreservesAcknowledgementAndSanitizesUncertainty(string commitFault)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        var job = UploadJob.Create("archive.zip", DateTimeOffset.UnixEpoch);
        job.Fail("Finished", DateTimeOffset.UnixEpoch);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.Add(UploadJobEntity.FromDomain(job));
            await db.SaveChangesAsync();
        }
        host.Faults.CommitFault = commitFault;
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PatchAsJsonAsync($"/api/jobs/{job.Id}", new UpdateJobRequest(true));

        if (commitFault == "none")
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var result = (await response.Content.ReadFromJsonAsync<ContractJob>()).ShouldNotBeNull();
            result.Id.ShouldBe(job.Id);
            result.IsArchived.ShouldBeTrue();
        }
        else
        {
            using var problem = await ProblemEnvelope.ReadAsync(response, 500, "General.Unhandled");
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldNotContain(host.Faults.Primary.Message);
            body.ShouldNotContain(host.Faults.Cleanup.Message);
        }
        response.Headers.ETag.ShouldBeNull();
        host.Faults.Disposals.ShouldBe(1);
        await using var verifyScope = host.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await verify.Jobs.SingleAsync(row => row.Id == job.Id)).IsArchived.ShouldBe(commitFault != "before");
    }

    [Fact]
    public async Task IgdbSettings_RealUpdateAndRecordedProbe_ReturnOwnEtagsDespiteCleanup_AndKeepPreconditions()
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        const string route = "/api/metadata-providers/igdb/settings";
        using var original = await client.GetAsync(route);
        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        string oldTag = original.Headers.ETag.ShouldNotBeNull().Tag;
        using var request = new HttpRequestMessage(HttpMethod.Put, route)
        { Content = JsonContent.Create(new UpdateIgdbProviderSettingsRequest(false, null, null, ClearClientSecret: true)) };
        request.Headers.TryAddWithoutValidation("If-Match", oldTag).ShouldBeTrue();

        using var saved = await client.SendAsync(request);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var settings = (await saved.Content.ReadFromJsonAsync<IgdbProviderSettings>()).ShouldNotBeNull();
        saved.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe($"\"{settings.Revision}\"");
        settings.HasClientSecret.ShouldBeFalse();
        settings.Enabled.ShouldBeFalse();
        host.Faults.Disposals.ShouldBe(1);
        using var test = new HttpRequestMessage(HttpMethod.Post, route + "/test-connection");
        test.Headers.TryAddWithoutValidation("If-Match", saved.Headers.ETag!.Tag).ShouldBeTrue();
        using var tested = await client.SendAsync(test);
        tested.StatusCode.ShouldBe(HttpStatusCode.OK);
        var observed = (await tested.Content.ReadFromJsonAsync<IgdbProviderSettings>()).ShouldNotBeNull();
        observed.LastTestedAt.ShouldNotBeNull();
        observed.LastTestSucceeded.ShouldBe(false);
        tested.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe($"\"{observed.Revision}\"");
        host.Faults.Disposals.ShouldBe(2);

        host.Faults.Enabled = false; // A business error retains ordinary cleanup-failure behavior, tested separately.
        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, route)
        { Content = JsonContent.Create(new UpdateIgdbProviderSettingsRequest(false, null, null)) };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", oldTag).ShouldBeTrue();
        using var stale = await client.SendAsync(staleRequest);
        using (await ProblemEnvelope.ReadAsync(stale, 412, "MetadataProviders.PreconditionFailed"))
        { }
        using var read = await client.GetAsync(route);
        read.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(tested.Headers.ETag!.Tag);
    }

    private sealed class SettlementFixture : IntegrationTestFixture
    {
        public FaultState Faults { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new FaultWork(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), Faults));
            });
        }
    }

    private sealed class FaultState
    {
        public bool Enabled { get; set; } = true;
        public string CommitFault { get; set; } = "none";
        public IOException Primary { get; } = new("Private original transaction failure");
        public IOException Cleanup { get; } = new("Private transaction disposal failure");
        public int Disposals { get; set; }
    }

    private sealed class FaultWork(IUnitOfWork inner, FaultState faults) : IUnitOfWork
    {
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct) => new Transaction(await inner.BeginTransactionAsync(ct), faults);
        public Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        private sealed class Transaction(ITransaction inner, FaultState faults) : ITransaction
        {
            public async Task CommitAsync(CancellationToken ct)
            {
                if (faults.CommitFault == "before")
                    throw faults.Primary;
                await inner.CommitAsync(ct);
                if (faults.CommitFault == "after")
                    throw faults.Primary;
            }
            public Task RollbackAsync(CancellationToken ct) => inner.RollbackAsync(ct);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                faults.Disposals++;
                if (faults.Enabled)
                    throw faults.Cleanup;
            }
        }
    }
}
