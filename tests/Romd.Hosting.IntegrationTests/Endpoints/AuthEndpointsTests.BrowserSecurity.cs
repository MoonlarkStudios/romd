using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class AuthEndpointsTests
{
    [Fact]
    public async Task Login_AntiforgeryRequiredBeforePasswordCheckAndCookieIssuance()
    {
        string email = $"csrf-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        var security = new AuthenticationSecurityTestServices();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => security.Configure(services)));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using var missing = await browser.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["login"] = email, ["password"] = "Password123" }));
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.Headers.TryGetValues("Set-Cookie", out _).ShouldBeFalse();
        security.Hasher.Verifications.ShouldBe(0);
        using var valid = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = email, ["password"] = "Password123" });
        var values = await valid.ReadAsStringAsync();
        using var attacker = factory.CreateClient(new() { AllowAutoRedirect = false });
        await attacker.GetAsync("/connect/login");
        using var mismatched = await attacker.PostAsync("/connect/login", new StringContent(values, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        mismatched.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        security.Hasher.Verifications.ShouldBe(0);
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using var accepted = await browser.PostAsync("/connect/login", valid);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        accepted.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith("romd_login="));
        security.Hasher.Verifications.ShouldBe(1);
    }

    [Theory]
    [InlineData("/\\attacker.invalid", "/")]
    [InlineData("//attacker.invalid", "/")]
    [InlineData("https://attacker.invalid", "/")]
    [InlineData("/\t/attacker.invalid", "/")]
    [InlineData("/\n/attacker.invalid", "/")]
    [InlineData("/\r/attacker.invalid", "/")]
    [InlineData("/\0/attacker.invalid", "/")]
    [InlineData("/api/me?from=login", "/api/me?from=login")]
    public async Task Login_ReturnUrl_OnlyRedirectsToFrameworkLocalPaths(string returnUrl, string expected)
    {
        string email = $"redirect-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        var security = new AuthenticationSecurityTestServices();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => security.Configure(services)));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = email, ["password"] = "Password123", ["returnUrl"] = returnUrl });
        using var response = await browser.PostAsync("/connect/login", form);
        if (returnUrl.Contains('\0'))
        {
            // ASP.NET refuses NUL while decoding form data, before redirect handling.
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Headers.Location.ShouldBeNull();
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.ShouldBe(expected);
        }
    }

    [Fact]
    public async Task Login_AccountDefaultBudget_AliasCaseCannotBypassAndWindowRecovers()
    {
        string email = $"throttle-{Guid.NewGuid():N}@localhost";
        await CreateAdminUserAsync(email, "Password123");
        var security = new AuthenticationSecurityTestServices();
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => security.Configure(services)));
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
            { ["login"] = attempt % 2 == 0 ? email : " " + email.ToUpperInvariant() + " ", ["password"] = "wrong" });
            (await browser.PostAsync("/connect/login", form)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        using var blockedForm = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = email, ["password"] = "Password123" });
        using var blocked = await browser.PostAsync("/connect/login", blockedForm);
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromMinutes(1));
        security.Hasher.Verifications.ShouldBe(10);
        security.Time.Advance(TimeSpan.FromMinutes(1));
        using var recoveredForm = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
        { ["login"] = email, ["password"] = "Password123" });
        (await browser.PostAsync("/connect/login", recoveredForm)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_IpDefaultBudget_HonorsOnlyTrustedProxyAddresses(bool trusted)
    {
        var security = new AuthenticationSecurityTestServices();
        using var factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Romd:ForwardedHeaders:Enabled", "true");
            builder.UseSetting("Romd:ForwardedHeaders:KnownProxies:0", "192.0.2.10");
            builder.ConfigureTestServices(services => security.Configure(services, trusted ? "192.0.2.10" : "192.0.2.100"));
        });
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        for (int attempt = 0; attempt < 31; attempt++)
        {
            browser.DefaultRequestHeaders.Remove("X-Forwarded-For");
            browser.DefaultRequestHeaders.Add("X-Forwarded-For", trusted ? "198.51.100.1" : "198.51.100." + (attempt + 1));
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
            { ["login"] = "unknown-" + attempt, ["password"] = "wrong" });
            using var response = await browser.PostAsync("/connect/login", form);
            response.StatusCode.ShouldBe(attempt == 30 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized);
            if (attempt == 30)
                response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromMinutes(1));
        }
        security.Hasher.Verifications.ShouldBe(0);
        if (trusted)
        {
            browser.DefaultRequestHeaders.Remove("X-Forwarded-For");
            browser.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.2");
            using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/login", new Dictionary<string, string>
            { ["login"] = "unknown-new", ["password"] = "wrong" });
            (await browser.PostAsync("/connect/login", form)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }
}
