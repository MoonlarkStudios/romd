using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Romd.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Fact]
    public async Task DeviceVerification_AntiforgeryRequiredAndCookieFreeTokenExchangeStillWorks()
    {
        var security = new AuthenticationSecurityTestServices();
        await using var fixture = await ConsumerHostFixture.CreateAsync(services => security.Configure(services));
        await fixture.CreateConsumerUserAsync("csrf-player", "csrf-player@localhost", "Password123");
        using var browser = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        { ["client_id"] = RomdOpenIddictClients.Console, ["scope"] = "offline_access email profile roles" }));
        var codes = await ReadJsonObjectAsync(device);
        var values = new Dictionary<string, string>
        { ["user_code"] = RequiredString(codes, "user_code"), ["login"] = "csrf-player", ["password"] = "Password123" };
        browser.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using var missing = await browser.PostAsync("/connect/verify", CreateFormContent(values));
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        security.Hasher.Verifications.ShouldBe(0);
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", values);
        using var attacker = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        await attacker.GetAsync("/connect/verify?user_code=" + Uri.EscapeDataString(values["user_code"]));
        using var mismatched = await attacker.PostAsync("/connect/verify", new StringContent(await form.ReadAsStringAsync(), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        mismatched.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        security.Hasher.Verifications.ShouldBe(0);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<RomdDbContext>().AccountSessions.CountAsync()).ShouldBe(0);
        using (var pending = await browser.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(codes, "device_code"))))
            RequiredString(await ReadJsonObjectAsync(pending), "error").ShouldBe("authorization_pending");
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        (await browser.PostAsync("/connect/verify", form)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        security.Hasher.Verifications.ShouldBe(1);
        using var deviceClient = fixture.Factory.CreateClient(new() { HandleCookies = false });
        using var exchanged = await deviceClient.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(codes, "device_code")));
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        RequiredString(await ReadJsonObjectAsync(exchanged), "access_token").ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConsumerSignIn_DefaultAccountBudget_IsSharedAcrossLoginDeviceAndUsernameEmailAliases()
    {
        var security = new AuthenticationSecurityTestServices();
        await using var fixture = await ConsumerHostFixture.CreateAsync(services => security.Configure(services));
        var user = await fixture.CreateConsumerUserAsync("throttled-player", "throttled-player@localhost", "Password123");
        using var browser = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        { ["client_id"] = RomdOpenIddictClients.Console, ["scope"] = "offline_access email profile roles" }));
        var codes = await ReadJsonObjectAsync(device);
        string code = RequiredString(codes, "user_code");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string path = attempt % 2 == 0 ? "/connect/login" : "/connect/verify";
            var values = new Dictionary<string, string>
            { ["login"] = attempt % 2 == 0 ? user.UserName! : " " + user.Email!.ToUpperInvariant() + " ", ["password"] = "wrong", ["user_code"] = code };
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, path, values);
            (await browser.PostAsync(path, form)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        foreach (string path in new[] { "/connect/login", "/connect/verify" })
        {
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, path, new Dictionary<string, string>
            { ["login"] = user.UserName!, ["password"] = "Password123", ["user_code"] = code });
            using var response = await browser.PostAsync(path, form);
            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromMinutes(1));
        }
        security.Hasher.Verifications.ShouldBe(10);
        security.Time.Advance(TimeSpan.FromSeconds(59));
        using var blockedForm = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = user.Email!, ["password"] = "Password123" });
        using var blocked = await browser.PostAsync("/connect/login", blockedForm);
        blocked.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        security.Time.Advance(TimeSpan.FromSeconds(1));
        using var recoveredForm = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", new Dictionary<string, string>
        { ["login"] = user.Email!, ["password"] = "Password123", ["user_code"] = code });
        (await browser.PostAsync("/connect/verify", recoveredForm)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var exchange = await browser.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(codes, "device_code")));
        var tokens = await ReadJsonObjectAsync(exchange);
        using var authenticated = fixture.Factory.CreateClient(new() { HandleCookies = false });
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequiredString(tokens, "access_token"));
        (await authenticated.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
            { ["login"] = user.UserName!, ["password"] = "wrong" });
            (await browser.PostAsync("/connect/login", form)).StatusCode.ShouldBe(attempt == 9 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized);
        }
        (await authenticated.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await authenticated.PostAsync("/connect/token", CreateRefreshTokenRequest(RequiredString(tokens, "refresh_token")))).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Throttling never mutates persisted lockout state or revokes sessions.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var after = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        after!.AccessFailedCount.ShouldBe(0);
        after.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task ConsumerSignIn_DefaultIpBudget_CannotBeBypassedWithForwardedHeaderOrDifferentLoginNames()
    {
        var security = new AuthenticationSecurityTestServices();
        await using var fixture = await ConsumerHostFixture.CreateAsync(services => security.Configure(services));
        using var browser = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        { ["client_id"] = RomdOpenIddictClients.Console, ["scope"] = "offline_access email profile roles" }));
        string code = RequiredString(await ReadJsonObjectAsync(device), "user_code");
        for (int attempt = 0; attempt < 31; attempt++)
        {
            browser.DefaultRequestHeaders.Remove("X-Forwarded-For");
            browser.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100." + (attempt + 1));
            string path = attempt % 2 == 0 ? "/connect/login" : "/connect/verify";
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, path, new Dictionary<string, string>
            { ["login"] = "unknown-" + attempt, ["password"] = "wrong", ["user_code"] = code });
            using var response = await browser.PostAsync(path, form);
            response.StatusCode.ShouldBe(attempt == 30 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized);
            if (attempt == 30)
                response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromMinutes(1));
        }
        security.Hasher.Verifications.ShouldBe(0);
    }
    [Theory]
    [InlineData("/connect/login", false)]
    [InlineData("/connect/login", true)]
    [InlineData("/connect/verify", false)]
    [InlineData("/connect/verify", true)]
    public async Task InteractiveForm_ActualKestrel_RejectsBodiesOverSixtyFourKiB(string path, bool chunked)
    {
        var security = new AuthenticationSecurityTestServices();
        await using var fixture = await ConsumerHostFixture.CreateAsync(services => security.Configure(services), networkListener: true);
        using var addressClient = fixture.Factory.CreateClient();
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = addressClient.BaseAddress };
        var values = new Dictionary<string, string> { ["login"] = "unknown", ["password"] = "wrong", ["padding"] = new string('x', 65 * 1024) };
        if (path == "/connect/verify")
        {
            using var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
            { ["client_id"] = RomdOpenIddictClients.Console, ["scope"] = "offline_access email profile roles" }));
            values["user_code"] = RequiredString(await ReadJsonObjectAsync(device), "user_code");
        }
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, path, values);
        using var streaming = new UnknownLengthFormContent(await form.ReadAsByteArrayAsync());
        using var response = await browser.PostAsync(path, chunked ? streaming : form);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        security.Hasher.Verifications.ShouldBe(0);
    }

    [Fact]
    public async Task ProductionInteractiveForms_IssueSecureHttpOnlyCookiesWithExplicitSameSite()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(configureHost: builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Romd:DefaultAdminPassword", "ProductionCookieFixture123!");
        });
        await fixture.CreateConsumerUserAsync("cookie-player", "cookie-player@localhost", "Password123");
        using var browser = fixture.Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var get = await browser.GetAsync("/connect/login");
        string csrf = get.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("romd_csrf="));
        csrf.ShouldContain("secure");
        csrf.ShouldContain("httponly");
        csrf.ShouldContain("samesite=strict");
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = "cookie-player", ["password"] = "Password123" });
        using var response = await browser.PostAsync("/connect/login", form);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string login = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("romd_login="));
        login.ShouldContain("secure");
        login.ShouldContain("httponly");
        login.ShouldContain("samesite=lax");
    }

    private sealed class UnknownLengthFormContent : HttpContent
    {
        private readonly byte[] _body;
        public UnknownLengthFormContent(byte[] body)
        {
            _body = body;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_body).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

}
