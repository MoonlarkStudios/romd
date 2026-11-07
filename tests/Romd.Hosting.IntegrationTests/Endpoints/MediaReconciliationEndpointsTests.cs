using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Models.Media;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

using MediaReconciliationJob = Romd.Domain.Jobs.MediaReconciliationJob;
using MediaReconciliationSubject = Romd.Domain.Source.Rom.MediaReconciliationSubject;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class MediaReconciliationEndpointsTests
{
    private readonly IMediaReconciliationRequests requests = Substitute.For<IMediaReconciliationRequests>();
    private readonly IUnitOfWork work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction transaction = Substitute.For<ITransaction>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_MissingExplicitConversionChoice_RefusesBeforeTransaction(bool selection)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        object payload = selection ? new { RequestId = Guid.NewGuid(), Filter = "AllSubjects", Limit = 5 } : new { RequestId = Guid.NewGuid() };
        string endpoint = selection ? "/api/media/reconciliation/selections" : $"/api/media/reconciliation/subjects/LogicalRom/{IdCoder.Encode(7)}";
        using var response = await client.PostAsJsonAsync(endpoint, payload);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        requests.ReceivedCalls().ShouldBeEmpty();
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
    }

    [Fact]
    public async Task Policies_Admin_ReportsCurrentRegisteredKindRecipesAndHostReadCapability()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        var policies = (await client.GetFromJsonAsync<MediaReconciliationPolicy[]>("/api/media/reconciliation/policies")).ShouldNotBeNull();
        var gameCube = policies.Single(policy => policy.KindId == "gamecube-disc");
        gameCube.SystemKeys.ShouldBe(["gc"]);
        gameCube.CanDecode.ShouldBeTrue();
        gameCube.ConversionMode.ShouldBe("Automatic");
        var cd = policies.Single(policy => policy.ManifestType == "CdTracks");
        cd.ConversionMode.ShouldBe("Automatic");
        cd.CanDecode.ShouldBeFalse();
        var psp = policies.Single(policy => policy.KindId == "psp-umd");
        psp.SystemKeys.ShouldBe(["psp"]);
        psp.ConversionMode.ShouldBe("Automatic");
        requests.ReceivedCalls().ShouldBeEmpty();
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_Admin_ReturnsActualAcceptedJobAndExactRequestIdentity(bool created)
    {
        Guid actor = Guid.NewGuid(), requestId = Guid.NewGuid(), previous = Guid.NewGuid();
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.LogicalRom(7)), "retry", null, actor, DateTimeOffset.UtcNow);
        requests.StageRetryAsync(requestId, previous, actor, Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationAcceptance>)new MediaReconciliationAcceptance(job, created, false));
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(actor, roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync($"/api/media/reconciliation/{previous}/retry", new RetryMediaReconciliationRequest(requestId));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.ShouldBe($"/api/jobs/{job.Id}");
        response.Headers.ETag.ShouldBeNull();
        (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull().JobId.ShouldBe(job.Id);
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("LogicalRom", true)]
    [InlineData("OriginalLayout", true)]
    [InlineData("CatalogCdLayout", true)]
    [InlineData("CatalogCdLayout", false)]
    [InlineData("Representation", false)]
    public async Task Subject_Admin_CommitsExactTypedIntentAndReturnsActualJob(string kind, bool conversion)
    {
        Guid actor = Guid.NewGuid(), requestId = Guid.NewGuid();
        MediaReconciliationSubject subject = kind switch
        {
            "LogicalRom" => new MediaReconciliationSubject.LogicalRom(7),
            "OriginalLayout" => new MediaReconciliationSubject.OriginalLayout(7),
            "CatalogCdLayout" => new MediaReconciliationSubject.CatalogCdLayout(7),
            "Representation" => new MediaReconciliationSubject.Representation(7),
            _ => throw new ArgumentException("Unsupported subject fixture.", nameof(kind))
        };
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(subject), "subject", null, actor, DateTimeOffset.UtcNow);
        requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationAcceptance>)new MediaReconciliationAcceptance(job, true, false));
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(actor, roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync($"/api/media/reconciliation/subjects/{kind}/{IdCoder.Encode(7)}", new StartMediaReconciliationSubjectRequest(requestId, conversion));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.ShouldBe($"/api/jobs/{job.Id}");
        (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull().JobId.ShouldBe(job.Id);
        await requests.Received(1).StageSubjectAsync(Arg.Is<MediaReconciliationDemand>(d => d.RequestId == requestId && d.Subject.Equals(subject) &&
            d.ConversionRequested == conversion && d.Cause == MediaReconciliationCause.Admin && d.ActorId == actor && d.SelectionJobId == null), Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("AllSubjects", null, null, false, 7L)]
    [InlineData("KindSubjects", "gamecube-disc", "gc", true, 7L)]
    [InlineData("RetainedRepresentations", null, null, false, 7L)]
    [InlineData("AllSubjects", null, null, false, 8_589_934_588L)]
    public async Task Selection_Admin_ForwardsClosedScopeAndExplicitConversion(string filter, string? kind, string? system, bool conversion, long limit)
    {
        Guid actor = Guid.NewGuid(), requestId = Guid.NewGuid();
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(4, 5, 6, 0), limit, conversion), "selection", null, actor, DateTimeOffset.UtcNow);
        requests.StageSelectionAsync(requestId, Arg.Any<MediaReconciliationFilter>(), limit, conversion, MediaReconciliationCause.Admin, actor, Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationAcceptance>)new MediaReconciliationAcceptance(job, true, false));
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(actor, roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync("/api/media/reconciliation/selections", new StartMediaReconciliationSelectionRequest(requestId, filter, kind, system, limit, conversion));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var received = requests.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(IMediaReconciliationRequests.StageSelectionAsync));
        var actual = (MediaReconciliationFilter)received.GetArguments()[1]!;
        if (kind is not null)
        {
            actual.ShouldBeOfType<MediaReconciliationFilter.KindSubjects>().Kind.ShouldBe(kind);
            ((MediaReconciliationFilter.KindSubjects)actual).PlatformId.ShouldBe(9);
        }
        else
            actual.GetType().Name.ShouldBe(filter);
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(8_589_934_589L)]
    [InlineData(long.MaxValue)]
    public async Task Selection_OutsideFiniteNumericBounds_RefusesBeforeTransaction(long limit)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync("/api/media/reconciliation/selections",
            new StartMediaReconciliationSelectionRequest(Guid.NewGuid(), "AllSubjects", null, null, limit, false));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        requests.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Representation", true)]
    [InlineData("other", false)]
    public async Task Subject_InvalidNamespaceOrRepresentationConversion_RefusesBeforeTransaction(string kind, bool conversion)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync($"/api/media/reconciliation/subjects/{kind}/{IdCoder.Encode(7)}", new StartMediaReconciliationSubjectRequest(Guid.NewGuid(), conversion));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        requests.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("AllSubjects", "gamecube-disc", null, false)]
    [InlineData("AllSubjects", null, "gc", false)]
    [InlineData("RetainedRepresentations", null, null, true)]
    [InlineData("KindSubjects", "unknown", null, false)]
    [InlineData("other", null, null, false)]
    public async Task Selection_UnsupportedOrExtraneousScope_RefusesBeforeTransaction(string filter, string? kind, string? system, bool conversion)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync("/api/media/reconciliation/selections", new StartMediaReconciliationSelectionRequest(Guid.NewGuid(), filter, kind, system, 5, conversion));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        requests.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(RomdRoleType.Manager, HttpStatusCode.Forbidden)]
    [InlineData(RomdRoleType.User, HttpStatusCode.Forbidden)]
    public async Task Start_UnauthorizedRole_DoesNotStageIntent(RomdRoleType? role, HttpStatusCode expected)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (role is { } value)
            client.WithTestUser(Guid.NewGuid(), roles: [value]);
        using var response = await client.PostAsJsonAsync("/api/media/reconciliation/selections", new StartMediaReconciliationSelectionRequest(Guid.NewGuid(), "AllSubjects", null, null, 5, false));
        response.StatusCode.ShouldBe(expected);
        requests.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_StoreConflict_RollsBackAndPreservesProblemCode()
    {
        Guid actor = Guid.NewGuid(), requestId = Guid.NewGuid(), jobId = Guid.NewGuid();
        requests.StageRetryAsync(requestId, jobId, actor, Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationAcceptance>)Error.Conflict("MediaReconciliation.NotComplete", "Wait for settlement."));
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(actor, roles: [RomdRoleType.Admin]);
        using var response = await client.PostAsJsonAsync($"/api/media/reconciliation/{jobId}/retry", new RetryMediaReconciliationRequest(requestId));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("MediaReconciliation.NotComplete");
        await transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData("subject", "Provide an exact request identity and supported subject. Retained representations only request verification.")]
    [InlineData("selection", "Provide an exact request identity and a positive bounded selection limit.")]
    [InlineData("retry", "Provide exact request and previous job identities.")]
    public async Task Start_InvalidRequest_PreservesExistingProblemEnvelope(string operation, string detail)
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);
        string endpoint = operation switch
        {
            "subject" => $"/api/media/reconciliation/subjects/LogicalRom/{IdCoder.Encode(7)}",
            "selection" => "/api/media/reconciliation/selections",
            _ => $"/api/media/reconciliation/{Guid.NewGuid()}/retry"
        };
        object payload = operation switch
        {
            "subject" => new StartMediaReconciliationSubjectRequest(Guid.Empty, false),
            "selection" => new StartMediaReconciliationSelectionRequest(Guid.Empty, "AllSubjects", null, null, 5, false),
            _ => new RetryMediaReconciliationRequest(Guid.Empty)
        };

        using var response = await client.PostAsJsonAsync(endpoint, payload);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().ShouldBe("MediaReconciliation.InvalidRequest");
        body.RootElement.GetProperty("detail").GetString().ShouldBe(detail);
        body.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();
        body.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        await work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        requests.ReceivedCalls().ShouldBeEmpty();
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTestAuthentication();
        builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        builder.Services.AddSingleton(work);
        builder.Services.AddSingleton(requests);
        builder.Services.AddScoped<ICommandHandler<StartMediaReconciliationSubjectCommand, Guid>, StartMediaReconciliationSubjectCommandHandler>();
        builder.Services.AddScoped<ICommandHandler<StartMediaReconciliationSelectionCommand, Guid>, StartMediaReconciliationSelectionCommandHandler>();
        builder.Services.AddScoped<ICommandHandler<RetryMediaReconciliationCommand, Guid>, RetryMediaReconciliationCommandHandler>();
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.ResolveSystemIdAsync("gc", Arg.Any<CancellationToken>()).Returns((int?)9);
        builder.Services.AddSingleton(catalog);
        var media = RomMediaPolicyComposition.Create();
        builder.Services.AddSingleton(media.Kinds);
        builder.Services.AddSingleton(media.DeliveryPolicy);
        var capabilities = Substitute.For<Romd.Application.Common.Content.IMediaHostCapabilities>();
        capabilities.CanDecode("gamecube-disc").Returns(true);
        builder.Services.AddSingleton(capabilities);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api").MapMediaReconciliationEndpoints();
        return app;
    }
}
