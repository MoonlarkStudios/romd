using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Application.Common.Ids;
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
///     (docs/decisions/admin-api-contract-policy.md, "One Error Envelope" and "Strict Validation"):
///     every non-2xx admin body is RFC 9457 ProblemDetails plus errorCode/errors/traceId.
///     401 challenges are covered by the protected-by-default tests from #81 and stay
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
        using var body = await ReadProblemAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(400);
        root.GetProperty("errorCode").GetString().ShouldBe("Catalog.InvalidQuery");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errors").GetProperty("releaseCompleteness")[0].GetString()
            .ShouldNotBeNull().ShouldContain("bogus");
    }

    [Fact]
    public async Task SearchCatalog_MultipleInvalidParameters_PreservesAllFailures()
    {
        var response = await _client.GetAsync(
            "/api/catalog?releaseCompleteness=bogus&tracked=nope&sortBy=zzz&limit=500");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ReadProblemAsync(response);
        var errors = body.RootElement.GetProperty("errors");

        errors.TryGetProperty("releaseCompleteness", out _).ShouldBeTrue();
        errors.TryGetProperty("tracked", out _).ShouldBeTrue();
        errors.TryGetProperty("sortBy", out _).ShouldBeTrue();
        errors.TryGetProperty("limit", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchCatalog_OutOfRangeLimit_Returns400InsteadOfClamping()
    {
        var response = await _client.GetAsync("/api/catalog?limit=101");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ReadProblemAsync(response);
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
        using var body = await ReadProblemAsync(response);
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
        using var body = await ReadProblemAsync(response);
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
        using var body = await ReadProblemAsync(response);
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
        using var body = await ReadProblemAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(404);
        root.GetProperty("errorCode").GetString().ShouldBe("Collections.NotFound");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExportLibrary_ExportAlreadyInProgress_Returns409Envelope()
    {
        var scheduler = Substitute.For<IExportScheduler>();
        scheduler.EnqueueLibraryExportAsync(Arg.Any<ExportScope>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Guid.Empty);

        await using var factory = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IExportScheduler>();
                services.AddSingleton(scheduler);
            }));
        using var client = factory.CreateClient()
            .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);

        var response = await client.PostAsJsonAsync("/api/export/library", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await ReadProblemAsync(response);
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

        var response = await client.PostAsync("/api/admin/backfill-bios", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        string rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldNotContain(secretExceptionMessage);
        rawBody.ShouldNotContain(nameof(InvalidOperationException));

        using var body = JsonDocument.Parse(rawBody);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(500);
        root.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("errorCode").GetString().ShouldBe("General.Unhandled");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        if (root.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null)
        {
            throw new ShouldAssertException(
                $"500 responses must not carry exception details, but got: {detail}");
        }
    }

    [Fact]
    public async Task ConfirmExternalId_ConcurrentTitleWrite_Returns409WithoutPersistenceDetails()
    {
        const string privateDetail = "secret-title-concurrency-provider-detail";
        var titles = Substitute.For<ITitleRepository>();
        titles.GetWithCollectionsAsync(123, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PersistenceConflictException(new InvalidOperationException(privateDetail)));
        await using var factory = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITitleRepository>();
                services.AddSingleton(titles);
            }));
        using var client = factory.CreateClient()
            .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);

        var response = await client.PostAsync(
            $"/api/titles/{IdCoder.Encode(123)}/external-ids/igdb/confirm", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await ReadProblemAsync(response);
        var root = body.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(409);
        root.GetProperty("errorCode").GetString().ShouldBe("Catalog.TitleConflict");
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        string rawBody = root.GetRawText();
        rawBody.ShouldNotContain(privateDetail);
        rawBody.ShouldNotContain(nameof(PersistenceConflictException));
        rawBody.ShouldNotContain(nameof(InvalidOperationException));
        rawBody.ShouldNotContain("stackTrace");
    }

    private static async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
