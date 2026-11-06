using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>Wire regressions for the S6 canonical core resources; requests deliberately use JSON,
/// so these tests compile against the pre-migration contracts and establish semantic failures.</summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class S6CoreResourceContractTests(IntegrationTestFixture fixture)
{
    private const string IgdbSettings = "/api/metadata-providers/igdb/settings";

    [Fact]
    public async Task GetMe_AuthenticatedUser_ReturnsItsOpaqueIdentity()
    {
        var userId = await CreateUserAsync();
        using var client = fixture.CreateClient().WithTestUser(userId, roles: [RomdRoleType.Admin]);
        using var response = await client.GetAsync("/api/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(response);
        body.RootElement.GetProperty("id").GetString().ShouldBe(userId.ToString());
    }

    [Fact]
    public async Task PutMyPassword_ValidPassword_ReplacesPasswordState()
    {
        var userId = await CreateUserAsync();
        using var client = fixture.CreateClient().WithTestUser(userId, roles: [RomdRoleType.Admin]);
        using var response = await client.PutAsJsonAsync("/api/me/password",
            new { currentPassword = "Password123", newPassword = "Password456" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var user = (await users.FindByIdAsync(userId.ToString())).ShouldNotBeNull();
        (await users.CheckPasswordAsync(user, "Password123")).ShouldBeFalse();
        (await users.CheckPasswordAsync(user, "Password456")).ShouldBeTrue();
    }

    [Fact]
    public async Task PutMyPassword_MissingFields_PreservesEveryValidationFailure()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PutAsJsonAsync("/api/me/password",
            new { currentPassword = "", newPassword = "" });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Auth.MissingRequiredFields");
        var errors = problem.RootElement.GetProperty("errors");
        errors.GetProperty("currentPassword").GetArrayLength().ShouldBeGreaterThan(0);
        errors.GetProperty("newPassword").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ListMySessions_MultiplePages_DoesNotClipSecurityHistoryAtTwoHundred()
    {
        var userId = await CreateUserAsync();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var now = DateTimeOffset.UtcNow;
            for (int index = 0; index < 205; index++)
            {
                var session = new AccountSessionEntity
                {
                    UserId = userId,
                    ClientId = "romd-admin-spa",
                    Device = $"S6 session {index}",
                    AccountStamp = "retired-stamp",
                    CreatedAt = now.AddMinutes(-index),
                    LastUsedAt = now.AddMinutes(-index),
                    ExpiresAt = now.AddDays(1),
                    RevokedAt = now
                };
                db.AccountSessions.Add(session);
                expected.Add(session.Id.ToString());
            }
            await db.SaveChangesAsync();
        }
        using var client = fixture.CreateClient().WithTestUser(userId, roles: [RomdRoleType.Admin]);
        var actual = await TraverseAsync(client, "/api/me/sessions?limit=100", "id");

        actual.Count.ShouldBe(205);
        actual.Distinct(StringComparer.Ordinal).Count().ShouldBe(205);
        actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/me/sessions?limit=0", "limit")]
    [InlineData("/api/me/sessions?limit=101", "limit")]
    [InlineData("/api/me/sessions?cursor=not-a-cursor", "cursor")]
    [InlineData("/api/roms?status=unknown", "status")]
    [InlineData("/api/roms?status=CATALOGED", "status")]
    [InlineData("/api/original-disc-layouts?systemKey=unknown-s6-system", "systemKey")]
    [InlineData("/api/original-disc-layouts?limit=0", "limit")]
    [InlineData("/api/original-disc-layouts?cursor=not-a-cursor", "cursor")]
    public async Task ListResources_InvalidSuppliedParameter_ReturnsFieldKeyedProblem(string path, string parameter)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(path);

        string code = path == "/api/me/sessions?cursor=not-a-cursor" ? "Account.InvalidCursor" : "Request.InvalidParameter";
        using var problem = await ProblemEnvelope.ReadAsync(response, 400, code);
        problem.RootElement.GetProperty("errors").GetProperty(parameter).GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ListRoms_OversizedQuery_ReturnsEnvelopedValidation()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/roms?query=" + new string('x', 201));

        using var problem = await ProblemEnvelope.ReadAsync(response);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("errors").GetProperty("query").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task BatchDeleteRoms_PostCanonicalAction_RejectsEmptyIdentitySelection()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/roms/batch-delete", new { romIds = Array.Empty<string>() });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Roms.InvalidRomIds");
        problem.RootElement.GetProperty("errors").GetProperty("romIds").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("/api/server/storage", "totalStorageBytes")]
    [InlineData("/api/server/health", "totalRoms")]
    [InlineData("/api/server/diagnostics", "storage")]
    [InlineData("/api/catalog/stats/coverage", "expectedTitleCount")]
    public async Task ServerResource_CanonicalPath_ReturnsItsProjection(string path, string member)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(response);
        body.RootElement.TryGetProperty(member, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task DashboardStatusBar_Composite_ContainsAllThreeCurrentProjections()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/dashboard/status-bar");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(response);
        body.RootElement.GetProperty("storage").GetProperty("totalStorageBytes").ValueKind.ShouldBe(JsonValueKind.String);
        body.RootElement.GetProperty("coverage").GetProperty("expectedTitleCount").ValueKind.ShouldBe(JsonValueKind.Number);
        body.RootElement.GetProperty("health").GetProperty("totalRoms").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public async Task MediaPolicies_CanonicalPath_RemainsProtected()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/media/reconciliation/policies");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(challenge => challenge.Scheme).ShouldContain("Bearer");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task IgdbSettings_Read_EmitsPreconditionValidatorWithoutClaimingRepresentationRevalidation()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(IgdbSettings);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        using var body = await ReadJsonAsync(response);
        etag.Tag.ShouldBe($"\"{body.RootElement.GetProperty("revision").GetGuid()}\"");
        using var request = new HttpRequestMessage(HttpMethod.Get, IgdbSettings);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag.Tag).ShouldBeTrue();
        using var reread = await client.SendAsync(request);
        reread.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("PUT", null, 428)]
    [InlineData("PUT", "*", 428)]
    [InlineData("PUT", "malformed", 400)]
    [InlineData("PUT", "W/\"00000000-0000-0000-0000-000000000000\"", 412)]
    [InlineData("PUT", "stale", 412)]
    [InlineData("POST", null, 428)]
    [InlineData("POST", "*", 428)]
    [InlineData("POST", "malformed", 400)]
    public async Task IgdbSettings_WriteWithoutCurrentStrongValidator_RejectsWithoutMutation(string method, string? tag, int status)
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var before = await client.GetAsync(IgdbSettings);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        string initial = before.Headers.ETag.ShouldNotBeNull().Tag;
        if (tag == "stale")
        {
            tag = initial == "\"00000000-0000-0000-0000-000000000000\""
                ? "\"11111111-1111-1111-1111-111111111111\""
                : "\"00000000-0000-0000-0000-000000000000\"";
            tag.ShouldNotBe(initial);
        }
        using var request = Request(new HttpMethod(method), method == "POST" ? IgdbSettings + "/test-connection" : IgdbSettings,
            tag, method == "PUT" ? new { enabled = false, clientId = (string?)null, clearClientSecret = true } : null);

        using var response = await client.SendAsync(request);

        using var problem = await ProblemEnvelope.ReadAsync(response, status,
            status switch
            {
                400 => "Request.InvalidParameter",
                428 => "MetadataProviders.PreconditionRequired",
                _ => "MetadataProviders.PreconditionFailed"
            });
        if (status == 400)
            problem.RootElement.GetProperty("errors").TryGetProperty("If-Match", out _).ShouldBeTrue();
        using var after = await client.GetAsync(IgdbSettings);
        after.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe(initial);
    }

    [Fact]
    public async Task IgdbSettings_TestConnection_RotatesValidatorAndRejectsItsOldValidator()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var read = await client.GetAsync(IgdbSettings);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var clear = await client.SendAsync(Request(HttpMethod.Put, IgdbSettings,
            read.Headers.ETag.ShouldNotBeNull().Tag,
            new { enabled = false, clientId = (string?)null, clientSecret = (string?)null, clearClientSecret = true }));
        clear.StatusCode.ShouldBe(HttpStatusCode.OK);
        string original = clear.Headers.ETag.ShouldNotBeNull().Tag;
        using var tested = await client.SendAsync(Request(HttpMethod.Post, IgdbSettings + "/test-connection", original));

        tested.StatusCode.ShouldBe(HttpStatusCode.OK);
        tested.Headers.ETag.ShouldNotBeNull().Tag.ShouldNotBe(original);
        using var result = await ReadJsonAsync(tested);
        result.RootElement.GetProperty("lastTestSucceeded").GetBoolean().ShouldBeFalse();
        result.RootElement.GetProperty("lastTestedAt").ValueKind.ShouldBe(JsonValueKind.String);
        using var stale = await client.SendAsync(Request(HttpMethod.Put, IgdbSettings, original,
            new { enabled = false, clientId = (string?)null, clearClientSecret = true }));
        using var problem = await ProblemEnvelope.ReadAsync(stale, 412, "MetadataProviders.PreconditionFailed");
    }

    [Fact]
    public async Task ListDiscSets_PageTraversal_PreservesAllRowsAndBindsTitleFilter()
    {
        var seed = await SeedDiscSetsAsync(103);
        using var client = fixture.CreateAuthenticatedClient();
        string path = $"/api/disc-sets?titleId={seed.TitleId}&limit=50";
        var ids = await TraverseAsync(client, path, "id");
        ids.Count.ShouldBe(103);
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(103);
        ids.ToHashSet(StringComparer.Ordinal).SetEquals(seed.Ids).ShouldBeTrue();
        using var first = await client.GetAsync(path);
        using var body = await ReadJsonAsync(first);
        string cursor = body.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        cursor.ShouldNotBeNullOrWhiteSpace();
        using var changed = await client.GetAsync($"/api/disc-sets?titleId={seed.OtherTitleId}&cursor={Uri.EscapeDataString(cursor)}&limit=50");
        using var problem = await ProblemEnvelope.ReadAsync(changed, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").GetProperty("cursor").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task DiscSet_PatchRoot_RequiresValidatorAndReturnsSystemKeyAndAcceptedRevision()
    {
        var seed = await SeedDiscSetsAsync(1);
        using var client = fixture.CreateAuthenticatedClient();
        string path = "/api/disc-sets/" + seed.Ids.Single();
        using var read = await client.GetAsync(path);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        string revision = read.Headers.ETag.ShouldNotBeNull().Tag;
        using var body = await ReadJsonAsync(read);
        body.RootElement.GetProperty("systemKey").GetString().ShouldBe(seed.SystemKey);
        body.RootElement.TryGetProperty("platformId", out _).ShouldBeFalse();
        using var missing = await client.PatchAsJsonAsync(path, new { decision = "reject", reason = "S6 wire test" });
        missing.StatusCode.ShouldBe((HttpStatusCode)428);
        using (await ProblemEnvelope.ReadAsync(missing))
        { }
        using var saved = await client.SendAsync(Request(HttpMethod.Patch, path, revision,
            new { decision = "reject", reason = "S6 wire test" }));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        saved.Headers.ETag.ShouldNotBeNull().Tag.ShouldNotBe(revision);
        using var savedBody = await ReadJsonAsync(saved);
        savedBody.RootElement.GetProperty("decision").GetString().ShouldBe("rejected");
        saved.Headers.ETag!.Tag.ShouldBe($"\"{savedBody.RootElement.GetProperty("revision").GetGuid()}\"");
        using var stale = await client.SendAsync(Request(HttpMethod.Patch, path, revision, new { decision = "reset" }));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using (await ProblemEnvelope.ReadAsync(stale))
        { }
    }

    [Fact]
    public async Task OriginalLayout_PatchRoot_RequiresValidatorAndReturnsAcceptedRevision()
    {
        string id = await SeedLayoutAsync();
        using var client = fixture.CreateAuthenticatedClient();
        string path = "/api/original-disc-layouts/" + id;
        using var read = await client.GetAsync(path);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        string revision = read.Headers.ETag.ShouldNotBeNull().Tag;
        using var missing = await client.PatchAsJsonAsync(path, new { action = "reject", reason = "S6 wire test" });
        missing.StatusCode.ShouldBe((HttpStatusCode)428);
        using (await ProblemEnvelope.ReadAsync(missing))
        { }
        using var saved = await client.SendAsync(Request(HttpMethod.Patch, path, revision,
            new { action = "reject", reason = "S6 wire test" }));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(saved);
        body.RootElement.GetProperty("state").GetString().ShouldBe("rejected");
        body.RootElement.TryGetProperty("platformId", out _).ShouldBeFalse();
        saved.Headers.ETag.ShouldNotBeNull().Tag.ShouldBe($"\"{body.RootElement.GetProperty("revision").GetGuid()}\"");
        saved.Headers.ETag.Tag.ShouldNotBe(revision);
        using var stale = await client.SendAsync(Request(HttpMethod.Patch, path, revision, new { action = "detach" }));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using (await ProblemEnvelope.ReadAsync(stale))
        { }
    }

    [Fact]
    public async Task OriginalLayoutCandidates_UnknownLayout_ReturnsOpaqueNotFound()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/original-disc-layouts/{IdCoder.Encode(int.MaxValue)}/candidates");

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "OriginalLayout.Missing");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldNotContain(int.MaxValue.ToString());
    }

    [Fact]
    public async Task OriginalLayoutAudit_PageTraversal_PreservesAllHistoryAndBindsLayoutScope()
    {
        string id = await SeedLayoutAsync();
        string otherId = await SeedLayoutAsync();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            int layoutId = Sqid.Parse(id, null).Value;
            for (int index = 0; index < 103; index++)
            {
                var audit = new OriginalDiscLayoutAuditEntity
                {
                    OriginalDiscLayoutId = layoutId,
                    Revision = Guid.NewGuid(),
                    Action = "reject",
                    At = DateTimeOffset.UtcNow.AddMinutes(-index)
                };
                db.OriginalDiscLayoutAudit.Add(audit);
                expected.Add(audit.Revision.ToString());
            }
            await db.SaveChangesAsync();
        }
        using var client = fixture.CreateAuthenticatedClient();
        string path = $"/api/original-disc-layouts/{id}/audit?limit=50";
        var revisions = await TraverseAsync(client, path, "revision");

        revisions.Count.ShouldBe(103);
        revisions.Distinct(StringComparer.Ordinal).Count().ShouldBe(103);
        revisions.ToHashSet(StringComparer.Ordinal).SetEquals(expected).ShouldBeTrue();
        using var first = await client.GetAsync(path);
        using var body = await ReadJsonAsync(first);
        string cursor = body.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        cursor.ShouldNotBeNullOrWhiteSpace();
        using var changed = await client.GetAsync($"/api/original-disc-layouts/{otherId}/audit?cursor={Uri.EscapeDataString(cursor)}&limit=50");
        using var problem = await ProblemEnvelope.ReadAsync(changed, 400, "Request.InvalidParameter");
        problem.RootElement.GetProperty("errors").GetProperty("cursor").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task MoveGame_UnknownPathTitleAndUnknownGame_ReportsThePathTitleFirst()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string missing = IdCoder.Encode(int.MaxValue);
        using var response = await client.PostAsJsonAsync($"/api/titles/{missing}/games/{missing}/move",
            new { newTitleName = "S6 missing-title precedence" });

        using var problem = await ProblemEnvelope.ReadAsync(response, 404, "Catalog.TitleNotFound");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().Any(char.IsDigit).ShouldBeFalse();
    }

    [Theory]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("POST", "/api/auth/password")]
    [InlineData("GET", "/api/auth/sessions")]
    [InlineData("DELETE", "/api/auth/sessions")]
    [InlineData("GET", "/api/dashboard/storage")]
    [InlineData("GET", "/api/dashboard/coverage")]
    [InlineData("GET", "/api/dashboard/health")]
    [InlineData("GET", "/api/diagnostics/operational")]
    [InlineData("GET", "/api/system/stats")]
    [InlineData("GET", "/api/roms/library-summary")]
    [InlineData("DELETE", "/api/roms/batch")]
    [InlineData("GET", "/api/admin/metadata-providers/igdb")]
    [InlineData("GET", "/api/admin/media/convergence")]
    [InlineData("GET", "/api/admin/media/chd/reverification")]
    public async Task RetiredCoreRoutes_ReturnTheRoutingProblem(string method, string path)
    {
        fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Any(endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true)
            .ShouldBeFalse("The retired literal route must be absent from the endpoint map.");
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.SendAsync(Request(new HttpMethod(method), path, null,
            method is "GET" or "DELETE" ? null : new { }));

        if (path is "/api/roms/batch" or "/api/roms/library-summary")
        {
            using var invalidId = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
            invalidId.RootElement.GetProperty("errors").GetProperty("romId").GetArrayLength().ShouldBeGreaterThan(0);
            return;
        }
        ((int)response.StatusCode).ShouldBeOneOf(404, 405);
        using var problem = await ProblemEnvelope.ReadAsync(response);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBeOneOf("Request.RouteNotFound", "Request.MethodNotAllowed");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? tag, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (tag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", tag).ShouldBeTrue();
        return request;
    }

    private static Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.ParseAsync(response.Content.ReadAsStream());

    private static async Task<List<string>> TraverseAsync(HttpClient client, string path, string identity)
    {
        var ids = new List<string>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var response = await client.GetAsync(path + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var body = await ReadJsonAsync(response);
            var page = body.RootElement;
            page.EnumerateObject().Select(property => property.Name).Order().ShouldBe(new[] { "hasNextPage", "items", "nextCursor" });
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                string id = item.GetProperty(identity).GetString().ShouldNotBeNull();
                id.ShouldNotBeNullOrWhiteSpace();
                ids.Add(id);
            }
            bool hasNext = page.GetProperty("hasNextPage").GetBoolean();
            cursor = page.GetProperty("nextCursor").GetString();
            if (hasNext)
            {
                cursor.ShouldNotBeNullOrWhiteSpace();
                seenCursors.Add(cursor).ShouldBeTrue("A page must advance its opaque cursor.");
            }
            else
                cursor.ShouldBeNull();
        } while (cursor is not null);
        return ids;
    }

    private async Task<Guid> CreateUserAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        string email = $"s6-core-{Guid.NewGuid():N}@localhost";
        var user = new RomdUser { UserName = email, Email = email, EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow };
        var result = await users.CreateAsync(user, "Password123");
        result.Succeeded.ShouldBeTrue(string.Join(", ", result.Errors.Select(error => error.Description)));
        (await users.AddToRoleAsync(user, "Admin")).Succeeded.ShouldBeTrue();
        return user.Id;
    }

    private async Task<(string TitleId, string OtherTitleId, string SystemKey, HashSet<string> Ids)> SeedDiscSetsAsync(int count)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var system = await db.Platforms.FirstAsync(platform => platform.CanonicalKey == "psx");
        TitleEntity NewTitle() => new()
        {
            PlatformId = system.Id,
            Name = "S6 Disc " + Guid.NewGuid().ToString("N"),
            NormalizedName = Guid.NewGuid().ToString("N"),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var title = NewTitle();
        var other = NewTitle();
        db.Titles.AddRange(title, other);
        await db.SaveChangesAsync();
        var rows = Enumerable.Range(0, count).Select(index => new DiscSetEntity
        {
            PlatformId = system.Id,
            CatalogSourceId = 1,
            TitleIds = [title.Id],
            ProposalKey = Guid.NewGuid().ToString("N"),
            ProposalJson = JsonSerializer.Serialize(new DiscSetDefinition($"S6 set {index}", 1,
                [new("entry", "S6 Disc", "s6-disc", 1, title.Id)], [], "s6-wire-test"))
        }).ToArray();
        db.DiscSets.AddRange(rows);
        await db.SaveChangesAsync();
        return (IdCoder.Encode(title.Id), IdCoder.Encode(other.Id), system.CanonicalKey!, rows.Select(row => IdCoder.Encode(row.Id)).ToHashSet(StringComparer.Ordinal));
    }

    private async Task<string> SeedLayoutAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var file = new FileEntityPersistence { Sha256 = Sha256.FromBytes(RandomNumberGenerator.GetBytes(Sha256.ByteLength)), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        var layout = new OriginalDiscLayoutEntity
        {
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "s6-test.cue",
            EntryPoint = "s6-test.cue",
            DescriptorFileId = file.Id
        };
        db.OriginalDiscLayouts.Add(layout);
        await db.SaveChangesAsync();
        return IdCoder.Encode(layout.Id);
    }
}
