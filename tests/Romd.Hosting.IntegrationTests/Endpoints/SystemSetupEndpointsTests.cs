using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SystemSetupEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task StopSubscription_UnknownIdentityReturnsNotFound()
    {
        var id = new Romd.Application.Common.Ids.Sqid(int.MaxValue).ToString();
        using var client = fixture.CreateAuthenticatedClient();
        (await client.DeleteAsync($"/api/dat-subscriptions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("cursor=", "cursor")]
    [InlineData("cursor=malformed", "cursor")]
    [InlineData("limit=101", "limit")]
    [InlineData("limit=not-a-number", "limit")]
    [InlineData("query=first&query=second", "query")]
    [InlineData("unknown=1", "unknown")]
    [InlineData("query=!!!", "query")]
    [InlineData("query=%20%20", "query")]
    public async Task Directory_InvalidQuery_ReturnsField400BeforeServiceRead(string query, string field)
    {
        var service = Substitute.For<ISystemSetupService>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISystemSetupService>();
            services.AddSingleton(service);
        }));
        using var client = factory.CreateClient();
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();

        using var response = await client.GetAsync("/api/system-setup?" + query);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        await service.DidNotReceive().ListAsync(Arg.Any<ReferencePageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Directory_EnableDisable_PreservesPlatformAndAliases()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var systems = await TestHelpers.ReadAllPageItemsAsync<ManagedSystemDto>(client, "/api/system-setup");
        var psx = systems!.Single(x => x.Key == "psx");
        (await client.PutAsJsonAsync($"/api/system-setup/{psx.Key}/enabled", new { enabled = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/api/system-setup/{psx.Key}/enabled", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var hidden = (await TestHelpers.ReadAllPageItemsAsync<ManagedSystemDto>(client, "/api/system-setup"))!.Single(x => x.Key == psx.Key);
        hidden.Enabled.ShouldBeFalse();
        var aliasHit = await TestHelpers.ReadAllPageItemsAsync<ManagedSystemDto>(client, "/api/system-setup?query=PS1");
        aliasHit.ShouldContain(x => x.Key == psx.Key);
        (await client.GetAsync($"/api/systems/{psx.Key}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManualPreview_IsReadOnly_AndChangedDocumentCannotBeApproved()
    {
        using var client = fixture.CreateAuthenticatedClient();
        const string document = "<datafile><header><name>PS1</name></header><game name='Setup Test'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
        using var form = TestHelpers.CreateMultipartContent(document, "catalog.dat");
        var response = await client.PostAsync("/api/system-setup/dat-preview", form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<SystemDatPreviewDto>())!;
        preview.SuggestedSystemKey.ShouldNotBeNull();
        preview.Preview.CandidateEntries.ShouldBe(1);
        using var changed = TestHelpers.CreateMultipartContent(document.Replace("size='1'", "size='2'"), "changed.dat");
        var rejected = await client.PostAsync($"/api/system-setup/{preview.SuggestedSystemKey}/import?sha256={preview.Preview.CandidateSha256}", changed);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("errorCode").GetString().ShouldBe("SystemSetup.Stale");
    }

    [Fact]
    public async Task MalformedDat_ReturnsValidationError()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var invalid = TestHelpers.CreateMultipartContent("<datafile><header>", "broken.dat");
        (await client.PostAsync("/api/system-setup/dat-preview", invalid)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_ReviewedNonemptyDocument_ReturnsCanonicalAcceptedJobAndCommitsEnablementAndDispatch()
    {
        await using var host = new AcceptanceFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        string document = $"<datafile><header><name>PS1</name></header><game name='System acceptance {Guid.NewGuid():N}'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
        using var previewForm = TestHelpers.CreateMultipartContent(document, "selected.dat");
        using var previewResponse = await client.PostAsync("/api/system-setup/dat-preview", previewForm);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<SystemDatPreviewDto>()).ShouldNotBeNull();
        preview.Preview.CandidateEntries.ShouldBe(1);
        using var upload = TestHelpers.CreateMultipartContent(document, "selected.dat");

        using var accepted = await client.PostAsync($"/api/system-setup/psx/import?sha256={preview.Preview.CandidateSha256}", upload);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reference = (await accepted.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        reference.JobId.ShouldNotBe(Guid.Empty);
        accepted.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe($"/api/jobs/{reference.JobId}");
        accepted.Headers.ETag.ShouldBeNull();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var psx = await db.Platforms.SingleAsync(platform => platform.ShortName == "psx");
        psx.IsEnabled.ShouldBe(true);
        var job = await db.Jobs.OfType<UploadJobEntity>().SingleAsync(row => row.Id == reference.JobId);
        job.PlatformId.ShouldBe(psx.Id);
        job.SourceFilename.ShouldBe("reviewed-catalog.dat");
        job.MaxParallelRoms.ShouldBe(4);
        job.AllowUnidentified.ShouldBeFalse();
        job.ArchiveOnly.ShouldBeFalse();
        job.TrackedOnly.ShouldBeFalse();
        job.Phase.ShouldBe("Pending");
        job.CreatedByUserId.ShouldBe(Guid.Parse(client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserIdHeader).Single()));
        var dispatch = await db.JobDispatches.SingleAsync(row => row.JobId == reference.JobId);
        dispatch.JobType.ShouldBe("upload");
        (await File.ReadAllBytesAsync(Path.Combine(host.DataDirectoryPath, "temp", "jobs", $"{reference.JobId:N}", "reviewed-catalog.dat")))
            .ShouldBe(Encoding.UTF8.GetBytes(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_MissingOrNonAdminAuthentication_RejectsBeforeSystemResolutionAndCommand(bool authenticated)
    {
        var handler = Substitute.For<ICommandHandler<ImportSystemDatCommand, Guid>>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<ImportSystemDatCommand, Guid>>();
            services.AddSingleton(handler);
        }));
        using var client = factory.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), "player@localhost", [RomdRoleType.User]);
        using var form = TestHelpers.CreateMultipartContent("<datafile><header>", "invalid.dat");

        using var response = await client.PostAsync($"/api/system-setup/no-such-system/import?sha256={new string('0', 64)}", form);

        response.StatusCode.ShouldBe(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Import_UppercaseEquivalentPin_ReturnsStaleProblemEnvelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        const string document = "<datafile><header><name>PS1</name></header><game name='Case sensitive pin'><rom name='one.bin' size='1' crc='11111111'/></game></datafile>";
        using var previewForm = TestHelpers.CreateMultipartContent(document, "catalog.dat");
        using var response = await client.PostAsync("/api/system-setup/dat-preview", previewForm);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<SystemDatPreviewDto>()).ShouldNotBeNull();
        string uppercase = preview.Preview.CandidateSha256.ToUpperInvariant();
        uppercase.ShouldNotBe(preview.Preview.CandidateSha256);
        using var form = TestHelpers.CreateMultipartContent(document, "catalog.dat");

        using var rejected = await client.PostAsync($"/api/system-setup/psx/import?sha256={uppercase}", form);

        using var problem = await ProblemEnvelope.ReadAsync(rejected, 409, "SystemSetup.Stale");
        rejected.Headers.ETag.ShouldBeNull();
    }

    [Fact]
    public async Task Import_EmptyDocument_ReturnsFileProblemBeforeCommand()
    {
        var handler = Substitute.For<ICommandHandler<ImportSystemDatCommand, Guid>>();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommandHandler<ImportSystemDatCommand, Guid>>();
            services.AddSingleton(handler);
        }));
        using var client = factory.CreateClient();
        using var authenticated = fixture.CreateAuthenticatedClient();
        foreach (var header in authenticated.DefaultRequestHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();
        using var form = TestHelpers.CreateMultipartContent("", "empty.dat");

        using var rejected = await client.PostAsync($"/api/system-setup/psx/import?sha256={new string('0', 64)}", form);

        using var problem = await ProblemEnvelope.ReadAsync(rejected, 400, "SystemSetup.InvalidFile");
        problem.RootElement.GetProperty("errors").TryGetProperty("file", out _).ShouldBeTrue();
        handler.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownSystemKey_ForbiddenCaller_IsForbiddenBeforeResolution()
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), "player@localhost", [RomdRoleType.User]);

        using var response = await client.PutAsJsonAsync("/api/system-setup/no-such-system/enabled", new { enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnknownSystemKey_Returns404Envelope()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var enabled = await client.PutAsJsonAsync("/api/system-setup/no-such-system/enabled", new { enabled = true });
        using var enabledProblem = await ProblemEnvelope.ReadAsync(enabled, 404, "Systems.NotFound");

        using var dat = TestHelpers.CreateMultipartContent("<datafile><header>", "broken.dat");
        using var import = await client.PostAsync($"/api/system-setup/no-such-system/import?sha256={new string('0', 64)}", dat);
        using var importProblem = await ProblemEnvelope.ReadAsync(import, 404, "Systems.NotFound");
    }

    private sealed class AcceptanceFixture : IntegrationTestFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Keep acceptance observable in this fixture's own database, without executing the upload.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
