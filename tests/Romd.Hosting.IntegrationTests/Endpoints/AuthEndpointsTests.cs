using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Auth;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class AuthEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ChangePassword_AuthenticatedUser_ChangesPassword()
    {
        string email = $"admin-{Guid.NewGuid():N}@localhost";
        var userId = await CreateAdminUserAsync(email, "Password123");
        using var client = fixture.CreateClient().WithTestUser(userId, email, [RomdRoleType.Admin]);

        var response = await client.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest("Password123", "Password456"));
        var oldPasswordLogin = await SubmitConnectLoginAsync(email, "Password123");
        var newPasswordLogin = await SubmitConnectLoginAsync(email, "Password456");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        oldPasswordLogin.ShouldBe(HttpStatusCode.Unauthorized);
        newPasswordLogin.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsBadRequest()
    {
        string email = $"admin-{Guid.NewGuid():N}@localhost";
        var userId = await CreateAdminUserAsync(email, "Password123");
        using var client = fixture.CreateClient().WithTestUser(userId, email, [RomdRoleType.Admin]);

        var response = await client.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest("WrongPassword123", "Password456"));
        var unchangedPasswordLogin = await SubmitConnectLoginAsync(email, "Password123");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unchangedPasswordLogin.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ChangePassword_UnauthenticatedRequest_ReturnsUnauthorized()
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest("Password123", "Password456"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthorizationCode_AdminUser_TokenSatisfiesRequireAdminPolicy()
    {
        // Verifies IsInRole("Admin") works on an OpenIddict-validated principal (role-claim gate):
        // an admin obtains a token via the auth-code flow and reaches a RequireAdmin endpoint.
        string accessToken = await CreateAdminOpenIddictTokenAsync();

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.GetAsync("/api/users");

        accessToken.ShouldNotBeNullOrWhiteSpace();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HubQueryStringToken_OpenIddictAccessToken_AuthenticatesOnlyOnHubs()
    {
        // SignalR sends the (JWE) OpenIddict access token as a query parameter; it must authenticate
        // on /hubs but never be honored as a query token on a normal API route.
        string accessToken = await CreateAdminOpenIddictTokenAsync();
        using var client = fixture.CreateClient();

        var hubWithToken = await client.PostAsync(
            $"/hubs/jobs/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(accessToken)}",
            content: null);
        var hubWithoutToken = await client.PostAsync("/hubs/jobs/negotiate?negotiateVersion=1", content: null);
        var apiWithQueryToken = await client.GetAsync($"/api/users?access_token={Uri.EscapeDataString(accessToken)}");

        hubWithToken.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        hubWithoutToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        apiWithQueryToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("role")]
    [InlineData("password")]
    [InlineData("sessions")]
    [InlineData("library")]
    [InlineData("session")]
    public async Task AccountSecurityChange_RevokesAccessAndRefreshTokens(string action)
    {
        string email = $"revocation-{Guid.NewGuid():N}@localhost";
        var userId = await CreateAdminUserAsync(email, "Password123");
        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var issued = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa,
            "http://localhost:5137/auth/callback", email, "Password123");
        using var session = fixture.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["access_token"].GetString());
        (await session.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var admin = fixture.CreateAuthenticatedClient();
        var security = await admin.GetFromJsonAsync<AccountSecurityDto>($"/api/users/{userId}/security");
        security!.Sessions.Count.ShouldBe(1);
        Guid.TryParse(security.Sessions[0].Id, out _).ShouldBeTrue();
        var ownSessions = await session.GetFromJsonAsync<AccountSecurityDto>("/api/auth/sessions");
        ownSessions!.Sessions.Count.ShouldBe(1);
        using var change = action switch
        {
            "session" => await admin.DeleteAsync($"/api/users/{userId}/sessions?sessionId={Uri.EscapeDataString(security.Sessions[0].Id)}"),
            "suspend" => await admin.PutAsJsonAsync($"/api/users/{userId}/suspension", new SetAccountSuspensionRequest(true)),
            "role" => await admin.PutAsJsonAsync($"/api/users/{userId}/role", new AssignRoleRequest("User")),
            "password" => await admin.PutAsJsonAsync($"/api/users/{userId}", new UpdateUserRequest(null, "Password456")),
            "library" => await admin.PutAsJsonAsync($"/api/users/{userId}/library", new AssignLibraryRequest(null)),
            _ => await admin.DeleteAsync($"/api/users/{userId}/sessions")
        };
        change.IsSuccessStatusCode.ShouldBeTrue(await change.Content.ReadAsStringAsync());
        (await session.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refreshed = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = RomdOpenIddictClients.AdminSpa,
            ["refresh_token"] = issued["refresh_token"].GetString()!
        }));
        refreshed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        if (action == "suspend") (await SubmitConnectLoginAsync(email, "Password123")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RealOidcSubject_IsRecordedAsTheAuditActor_AndCannotSuspendItself()
    {
        string email = $"audit-{Guid.NewGuid():N}@localhost";
        var userId = await CreateAdminUserAsync(email, "Password123");
        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var issued = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa,
            "http://localhost:5137/auth/callback", email, "Password123");
        using var session = fixture.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["access_token"].GetString());
        var link = await session.PostAsJsonAsync($"/api/users/{userId}/links", new IssueAccountLinkRequest("recovery"));
        link.StatusCode.ShouldBe(HttpStatusCode.OK);
        var audit = await session.GetFromJsonAsync<AdminAuditPageDto>($"/api/audit?targetType=User&targetId={userId}");
        audit!.Items.Single(item => item.Action == "Recovery link issued").ActorId.ShouldBe(userId);
        (await session.PutAsJsonAsync($"/api/users/{userId}/suspension", new SetAccountSuspensionRequest(true)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Refresh_PreservesSessionIdentityAndAbsoluteExpiration()
    {
        string email = $"session-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var issued = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa,
            "http://localhost:5137/auth/callback", email, "Password123");
        using var session = fixture.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["access_token"].GetString());
        var before = await session.GetFromJsonAsync<AccountSecurityDto>("/api/auth/sessions");
        before!.Sessions.Single().IsCurrent.ShouldBeTrue();
        for (int i = 0; i < 3; i++)
        {
            var response = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "refresh_token", ["client_id"] = RomdOpenIddictClients.AdminSpa, ["refresh_token"] = issued["refresh_token"].GetString()! }));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            issued = (await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!;
            session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["access_token"].GetString());
        }
        var after = await session.GetFromJsonAsync<AccountSecurityDto>("/api/auth/sessions");
        after!.Sessions.Count.ShouldBe(1);
        after.Sessions[0].Id.ShouldBe(before.Sessions[0].Id);
        after.Sessions[0].CreatedAt.ShouldBe(before.Sessions[0].CreatedAt);
        after.Sessions[0].ExpiresAt.ShouldBe(before.Sessions[0].ExpiresAt);
    }

    [Fact]
    public async Task RevokeOthers_PreservesCaller_BlocksOtherTokensAndInteractiveCookie()
    {
        string email = $"others-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        using var first = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var second = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var firstTokens = await OidcAuthorizationCodeFlow.RunAsync(first, RomdOpenIddictClients.AdminSpa, "http://localhost:5137/auth/callback", email, "Password123");
        var secondTokens = await OidcAuthorizationCodeFlow.RunAsync(second, RomdOpenIddictClients.AdminSpa, "http://localhost:5137/auth/callback", email, "Password123");
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstTokens["access_token"].GetString());
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondTokens["access_token"].GetString());
        (await first.DeleteAsync("/api/auth/sessions?othersOnly=true")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await first.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var history = await first.GetFromJsonAsync<AccountSecurityDto>("/api/auth/sessions");
        history!.Sessions.Count(item => item.Status == "Current").ShouldBe(1);
        history.Sessions.Single(item => item.Status == "Current").IsCurrent.ShouldBeTrue();
        history.Sessions.Single(item => item.Status == "Revoked").RevocationReason.ShouldBe("Other sessions revoked");
        var refresh = await second.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = RomdOpenIddictClients.AdminSpa, ["refresh_token"] = secondTokens["refresh_token"].GetString()! }));
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        second.DefaultRequestHeaders.Authorization = null;
        var challenge = await second.GetAsync("/connect/authorize?client_id=romd-admin-spa&response_type=code&scope=openid&redirect_uri=http%3A%2F%2Flocalhost%3A5137%2Fauth%2Fcallback&code_challenge=" + OidcAuthorizationCodeFlow.CreateCodeChallenge(OidcAuthorizationCodeFlow.CreateCodeVerifier()) + "&code_challenge_method=S256");
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        challenge.Headers.Location!.OriginalString.ShouldStartWith("/connect/login");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitSignOut_EndsSessionAndAllItsTokens(bool protocolRevocation)
    {
        string email = $"logout-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var issued = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa, "http://localhost:5137/auth/callback", email, "Password123");
        if (protocolRevocation)
        {
            var response = await browser.PostAsync("/connect/revocation", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = RomdOpenIddictClients.AdminSpa, ["token"] = issued["refresh_token"].GetString()! }));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        else
        {
            var response = await browser.GetAsync("/connect/logout?id_token_hint=" + Uri.EscapeDataString(issued["id_token"].GetString()!));
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["access_token"].GetString());
        (await browser.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignOut_WithTokenRotatedDuringRequest_StillEndsSession()
    {
        string email = $"rotation-logout-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var issued = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa, "http://localhost:5137/auth/callback", email, "Password123");
        var refresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = RomdOpenIddictClients.AdminSpa, ["refresh_token"] = issued["refresh_token"].GetString()! }));
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = (await refresh.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!;
        var revoke = await browser.PostAsync("/connect/revocation", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = RomdOpenIddictClients.AdminSpa, ["token"] = issued["refresh_token"].GetString()! }));
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated["access_token"].GetString());
        (await browser.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<string> CreateAdminOpenIddictTokenAsync()
    {
        string email = $"admin-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");

        using var browser = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var tokens = await OidcAuthorizationCodeFlow.RunAsync(
            browser,
            RomdOpenIddictClients.AdminSpa,
            "http://localhost:5137/auth/callback",
            email,
            "Password123");

        return tokens["access_token"].GetString()!;
    }

    private async Task<Guid> CreateAdminUserAsync(string email, string password)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var user = new RomdUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var createResult = await userManager.CreateAsync(user, password);
        createResult.Succeeded.ShouldBeTrue(string.Join(", ", createResult.Errors.Select(error => error.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, RomdRoleType.Admin.ToString());
        roleResult.Succeeded.ShouldBeTrue(string.Join(", ", roleResult.Errors.Select(error => error.Description)));

        return user.Id;
    }

    // Verifies a password by attempting the interactive server login: a success redirects (302), bad
    // credentials re-render the login page with 401. AllowAutoRedirect is off so the 302 is observable.
    private async Task<HttpStatusCode> SubmitConnectLoginAsync(string login, string password)
    {
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var response = await client.PostAsync(
            "/connect/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = login,
                ["password"] = password
            }));

        return response.StatusCode;
    }
}
