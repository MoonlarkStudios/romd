using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.Queries.GetTitleEnrichmentState;
using Romd.Contracts.Management.Enrichment;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     End-to-end coverage for the shared error envelope
///     (docs/decisions/api-contract-policy.md, "One Error Envelope";
///     docs/decisions/api-contract-policy.md, "Strict Validation"):
///     every non-2xx admin body is RFC 9457 ProblemDetails plus errorCode/errors/traceId.
///     401 challenges are covered by the protected-by-default authorization tests and stay
///     authentication-middleware responses, so they are not duplicated here.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ErrorEnvelopeTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public ErrorEnvelopeTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task SearchCatalog_InvalidReleaseCompletenessFilter_Returns400Envelope()
    {
        var response = await _client.GetAsync("/api/catalog?releaseCompleteness=bogus");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Request.InvalidParameter");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("releaseCompleteness")[0].GetString()
            .ShouldBe("The value is not valid.");
    }

    [Fact]
    public async Task SearchCatalog_MultipleInvalidParameters_PreservesAllFailures()
    {
        var response = await _client.GetAsync(
            "/api/catalog?releaseCompleteness=bogus&tracked=nope&sortBy=zzz");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var errors = body.RootElement.GetProperty("errors");

        errors.TryGetProperty("releaseCompleteness", out _).ShouldBeTrue();
        errors.TryGetProperty("tracked", out _).ShouldBeTrue();
        errors.TryGetProperty("sortBy", out _).ShouldBeTrue();
    }

    // Parameter ranges are checked with binding, before the operation's own validation.
    [Fact]
    public async Task SearchCatalog_OutOfRangeLimit_Returns400InsteadOfClamping()
    {
        var response = await _client.GetAsync("/api/catalog?limit=101");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").GetProperty("limit")[0].GetString()
            .ShouldNotBeNull().ShouldContain("between 1 and 100");
    }

    [Fact]
    public async Task SearchCatalog_ValidAndAbsentFilterValues_KeepDocumentedDefaults()
    {
        var explicitValues = await _client.GetAsync(
            "/api/catalog?releaseCompleteness=all&tracked=all&sortBy=name&limit=50");
        var absentValues = await _client.GetAsync("/api/catalog");

        explicitValues.StatusCode.ShouldBe(HttpStatusCode.OK);
        absentValues.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SearchGames_InvalidBiosFilter_Returns400Envelope()
    {
        var response = await _client.GetAsync("/api/search/games?bios=bogus");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("errorCode").GetString().ShouldBe("Search.InvalidQuery");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("bios")[0].GetString()
            .ShouldNotBeNull().ShouldContain("exclude, include, only");
    }

    [Fact]
    public async Task SearchGames_ValidAndAbsentBiosValues_KeepDocumentedDefaults()
    {
        var explicitValues = await _client.GetAsync("/api/search/games?bios=exclude&sortBy=name");
        var absentValues = await _client.GetAsync("/api/search/games");

        explicitValues.StatusCode.ShouldBe(HttpStatusCode.OK);
        absentValues.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateUser_MissingEmailAndPassword_PreservesBothFailures()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/users",
            new { email = "", password = "", role = "User" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("errorCode").GetString().ShouldBe("Users.MissingRequiredFields");
        root.GetProperty("errors").GetProperty("email")[0].GetString().ShouldBe("Email is required.");
        root.GetProperty("errors").GetProperty("password")[0].GetString().ShouldBe("Password is required.");
    }

    [Fact]
    public async Task ExportLibrary_NonAdminRequestingSpecificLibrary_Returns403Envelope()
    {
        using var userClient = _fixture.CreateClient()
            .WithTestUser(Guid.NewGuid(), "user@localhost", [RomdRoleType.User]);

        var response = await userClient.PostAsJsonAsync(
            "/api/export/library",
            new { libraryId = IdCoder.Encode(123) });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(403);
        root.GetProperty("errorCode").GetString().ShouldBe("Export.AdminRoleRequired");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetCollection_UnknownId_Returns404Envelope()
    {
        var response = await _client.GetAsync($"/api/collections/{IdCoder.Encode(987654)}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(404);
        root.GetProperty("errorCode").GetString().ShouldBe("Collections.NotFound");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExportLibrary_ExportAlreadyInProgress_Returns409Envelope()
    {
        var scheduler = Substitute.For<ICommandHandler<QueueLibraryExportCommand, Guid>>();
        scheduler.HandleAsync(Arg.Any<QueueLibraryExportCommand>(), Arg.Any<CancellationToken>())
            .Returns(ExportErrors.AlreadyInProgress());

        await using var factory = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICommandHandler<QueueLibraryExportCommand, Guid>>();
                services.AddSingleton(scheduler);
            }));
        using var client = factory.CreateClient()
            .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);

        var response = await client.PostAsJsonAsync("/api/export/library", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await ProblemEnvelope.ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(409);
        root.GetProperty("errorCode").GetString().ShouldBe("Export.AlreadyInProgress");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnhandledException_Returns500EnvelopeWithoutExceptionDetails()
    {
        const string secretExceptionMessage = "secret-backfill-failure-detail";
        var datRepository = Substitute.For<IDatRepository>();
        datRepository.BackfillIsBiosAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(secretExceptionMessage));

        await using var factory = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDatRepository>();
                services.AddSingleton(datRepository);
            }));
        using var client = factory.CreateClient()
            .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);

        var response = await client.PostAsync("/api/bios/backfill", content: null);

        await ProblemEnvelope.AssertSanitizedServerErrorAsync(
            response,
            secretExceptionMessage,
            typeof(InvalidOperationException));
    }

    [Fact]
    public async Task PersistenceConflictException_BypassingApplicationBoundary_Returns500Envelope()
    {
        // Title handlers convert a stale snapshot into 412 or 409 themselves, so a conflict that still escapes
        // is an unexpected failure like any other.
        const string privateDetail = "secret-title-concurrency-provider-detail";
        var handler = Substitute.For<IQueryHandler<GetTitleEnrichmentStateQuery, TitleEnrichmentStateResponse>>();
        handler.HandleAsync(Arg.Any<GetTitleEnrichmentStateQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PersistenceConflictException(new InvalidOperationException(privateDetail)));
        await using var factory = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQueryHandler<GetTitleEnrichmentStateQuery, TitleEnrichmentStateResponse>>();
                services.AddSingleton(handler);
            }));
        using var client = factory.CreateClient()
            .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/titles/{IdCoder.Encode(123)}/enrichment");

        var response = await client.SendAsync(request);

        await ProblemEnvelope.AssertSanitizedServerErrorAsync(
            response,
            privateDetail,
            typeof(PersistenceConflictException));
    }
}
