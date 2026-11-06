using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Security;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class AuthEndpointsTests
{
    [Fact]
    public async Task Authorization_SessionCommitsBeforeCookieAndProtocol_ValidCookieReusesAndRevokedCookieCannotRemint()
    {
        string email = $"accept-session-{Guid.NewGuid():N}@localhost";
        var user = await CreateAdminUserAsync(email, "Password123");
        var probe = new AccountSessionIssuanceProbe();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(probe.Configure));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var tokens = await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.AdminSpa,
            "http://localhost:5137/auth/callback", email, "Password123");
        tokens.ContainsKey("access_token").ShouldBeTrue();
        var session = probe.Accepted.ShouldHaveSingleItem();
        session.UserId.ShouldBe(user);
        probe.Starts.ShouldBe(1);
        probe.IssuanceAttempts.ShouldContain(item => item.Scheme == Romd.Hosting.RomdHostRegistrationExtensions.InteractiveLoginScheme && item.SessionId == session.Id);
        probe.IssuanceAttempts.ShouldContain(item => item.Scheme == OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreDefaults.AuthenticationScheme && item.SessionId == session.Id);

        using var reused = await browser.GetAsync(SessionAuthorizeUrl());
        reused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        OidcAuthorizationCodeFlow.ParseQueryValue(reused.Headers.Location!, "code").ShouldNotBeNullOrEmpty();
        probe.Starts.ShouldBe(1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountSessions>().RevokeAsync(user, session.Id, null, user, "Test revoke", default);
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var row = await db.AccountSessions.SingleAsync(item => item.Id == session.Id);
            row.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAt.ToUnixTimeMilliseconds()));
        }
        using var rejected = await browser.GetAsync(SessionAuthorizeUrl());
        rejected.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        rejected.Headers.Location!.OriginalString.ShouldStartWith("/connect/login");
        probe.Starts.ShouldBe(1);
    }

    [Theory]
    [InlineData(SessionAcceptanceFault.BeforeAcceptance)]
    [InlineData(SessionAcceptanceFault.InteractiveCookie)]
    [InlineData(SessionAcceptanceFault.ProtocolIssuance)]
    public async Task Authorization_AcceptanceOrLaterIssuanceFault_PreservesDatabaseCommitBoundary(SessionAcceptanceFault fault)
    {
        string email = $"fault-session-{Guid.NewGuid():N}@localhost";
        var user = await CreateAdminUserAsync(email, "Password123");
        var probe = new AccountSessionIssuanceProbe { Fault = fault };
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(probe.Configure));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        string authorize = SessionAuthorizeUrl();
        using var login = await browser.PostAsync("/connect/login", await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login",
            new Dictionary<string, string> { ["returnUrl"] = authorize, ["login"] = email, ["password"] = "Password123" }));
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        using var failed = await browser.GetAsync(authorize);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        failed.Headers.Location.ShouldBeNull();
        probe.Starts.ShouldBe(1);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int expected = fault == SessionAcceptanceFault.BeforeAcceptance ? 0 : 1;
        (await db.AccountSessions.CountAsync(row => row.UserId == user)).ShouldBe(expected);
        probe.Accepted.Count.ShouldBe(expected);
        if (fault != SessionAcceptanceFault.ProtocolIssuance)
            failed.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeFalse();
        if (fault == SessionAcceptanceFault.ProtocolIssuance)
        {
            // Cookie dispatch happened first, but the failed HTTP response does not promise cookie delivery.
            probe.IssuanceAttempts.ShouldContain(item => item.Scheme == Romd.Hosting.RomdHostRegistrationExtensions.InteractiveLoginScheme);
            probe.IssuanceAttempts.Last().Scheme.ShouldBe(OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
    }

    private static string SessionAuthorizeUrl() => "/connect/authorize?client_id=romd-admin-spa&response_type=code&scope=openid%20offline_access"
        + "&redirect_uri=http%3A%2F%2Flocalhost%3A5137%2Fauth%2Fcallback&code_challenge="
        + OidcAuthorizationCodeFlow.CreateCodeChallenge(OidcAuthorizationCodeFlow.CreateCodeVerifier()) + "&code_challenge_method=S256";
}
