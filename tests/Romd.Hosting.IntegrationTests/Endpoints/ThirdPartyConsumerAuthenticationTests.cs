using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Management.ConsumerApps;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;
using DeliveryFixture = Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture;
using DeliverySeed = Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests.DeliverySeed;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class ThirdPartyConsumerAuthenticationTests
{
    private const string Login = "household-player";
    private const string Password = "Password123";
    private const string BrowserOrigin = "https://community.example";
    private const string OtherOrigin = "https://other-app.example";
    private const string WrongOrigin = "https://unapproved.example";
    private const string Registrations = "/api/consumer-web-apps";
    private const string AllConsumerScopes = "offline_access romd:discovery romd:library romd:releases romd:delivery";
    private static readonly WebApplicationFactoryClientOptions BrowserOptions = new()
    {
        BaseAddress = new Uri("https://consumer.example"),
        AllowAutoRedirect = false,
        HandleCookies = true
    };

    [Fact]
    public async Task PairDevice_OnStockServer_RevokeOneSessionAndKeepTheOtherAfterRestart()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        var user = await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);

        using var browser = fixture.Factory.CreateClient(BrowserOptions);
        var first = await PairAsync(browser, "Community app", "Living room", AllConsumerScopes);
        var second = await PairAsync(browser, "Community app", "Handheld", AllConsumerScopes);
        var limited = await PairAsync(browser, "Community app", "Read-only", "romd:library");
        using var admin = fixture.CreateAdminHost();
        using var adminClient = admin.CreateClient(BrowserOptions);

        using (var device = CreateConsumerClient(fixture, first.AccessToken))
        {
            await AssertLibraryAndContentAsync(device, seed);
            await AssertRestrictedAsync(device, adminClient);
        }
        using (var readOnly = CreateConsumerClient(fixture, limited.AccessToken))
        {
            (await readOnly.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await readOnly.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await readOnly.PostAsync($"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest", null))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var account = await FirstPartySignInAsync(browser);
        using var portal = CreateConsumerClient(fixture, account.AccessToken);
        var sessions = await GetSessionsAsync(portal);
        var livingRoom = sessions.Single(session => session.Device == "Community app · Living room");
        var handheld = sessions.Single(session => session.Device == "Community app · Handheld");
        livingRoom.Id.ShouldNotBe(handheld.Id);
        sessions.Single(session => session.IsCurrent).Id.ShouldNotBe(livingRoom.Id);
        (await portal.DeleteAsync($"/api/me/sessions/{livingRoom.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await fixture.RestartConsumerHostAsync();
        using var revoked = CreateConsumerClient(fixture, first.AccessToken);
        using var surviving = CreateConsumerClient(fixture, second.AccessToken);
        using var restarted = fixture.Factory.CreateClient(BrowserOptions);
        (await revoked.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(restarted, RomdOpenIddictClients.CommunityDevice, first.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await surviving.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(restarted, RomdOpenIddictClients.CommunityDevice, second.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.ReassignUserAsync(user.Id, seed.OtherLibraryId);
        var updatedCatalog = (await surviving.GetFromJsonAsync<Page<ConsumerTitleCard>>("/api/catalog")).ShouldNotBeNull();
        updatedCatalog.Items.Select(item => item.Name).ShouldBe(["EarthBound"]);
    }

    [Fact]
    public async Task RegisteredSpa_UsesExactCallbackAndOrigin_AndDisableInvalidatesTokensAndPendingCode()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        var user = await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);
        await using var adminHost = fixture.CreateAdminHost();
        using var anonymous = adminHost.CreateClient(BrowserOptions);
        using var operatorClient = adminHost.CreateClient(BrowserOptions);
        operatorClient.WithTestUser(user.Id, "operator@example.com", [RomdRoleType.Admin]);
        using var member = adminHost.CreateClient(BrowserOptions);
        member.WithTestUser(user.Id, user.Email!, [RomdRoleType.User]);

        (await anonymous.GetAsync(Registrations)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await member.PostAsJsonAsync(Registrations, Registration(BrowserOrigin))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await operatorClient.PostAsJsonAsync(Registrations,
            new RegisterConsumerWebAppRequest("Unsafe", "http://community.example/callback", "https://*.example")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var primary = await RegisterAsync(operatorClient, BrowserOrigin);
        var other = await RegisterAsync(operatorClient, OtherOrigin);
        using var browser = fixture.Factory.CreateClient(BrowserOptions);

        var attempt = CreateAuthorization(primary);
        (await browser.GetAsync(attempt.Url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PreflightAsync(browser, BrowserOrigin)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        await using var workerServices = fixture.CreateWorkerIdentityServices();
        using var worker = new ConsumerWebAppProvisioningWorker(workerServices.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, workerServices.GetRequiredService<ILogger<ConsumerWebAppProvisioningWorker>>());
        await worker.StartAsync(default);
        await WaitForRegistrationsAsync(operatorClient, primary.ClientId, other.ClientId);

        using var acceptedPreflight = await PreflightAsync(browser, BrowserOrigin);
        acceptedPreflight.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AssertCors(acceptedPreflight, BrowserOrigin);
        (await PreflightAsync(browser, WrongOrigin)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        (await browser.GetAsync(CreateAuthorization(primary with { CallbackUri = primary.CallbackUri + "/extra" }).Url))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.GetAsync(CreateAuthorization(primary, method: "plain").Url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (var excessiveScope = await browser.GetAsync(CreateAuthorization(primary, scopes: "openid roles").Url))
        {
            excessiveScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await excessiveScope.Content.ReadAsStringAsync()).ShouldContain("specified scope");
        }

        string code = await GetCodeAsync(browser, attempt);
        (await ExchangeAsync(browser, primary, attempt, code, WrongOrigin)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ExchangeAsync(browser, primary, attempt, code, OtherOrigin)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var exchange = await ExchangeAsync(browser, primary, attempt, code, BrowserOrigin);
        AssertCors(exchange, BrowserOrigin);
        var tokens = await ReadTokensAsync(exchange);
        using (var app = CreateConsumerClient(fixture, tokens.AccessToken, BrowserOrigin))
        {
            await AssertLibraryAndContentAsync(app, seed);
            await AssertRestrictedAsync(app, anonymous);
        }
        using (var wrong = CreateConsumerClient(fixture, tokens.AccessToken, OtherOrigin))
            (await wrong.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var second = CreateAuthorization(other);
        using var otherExchange = await ExchangeAsync(browser, other, second, await GetCodeAsync(browser, second), OtherOrigin);
        var otherTokens = await ReadTokensAsync(otherExchange);

        await worker.StopAsync(default);
        await fixture.RestartConsumerHostAsync();
        using var restarted = fixture.Factory.CreateClient(BrowserOptions);
        using var existing = CreateConsumerClient(fixture, tokens.AccessToken, BrowserOrigin);
        using var survivingApp = CreateConsumerClient(fixture, otherTokens.AccessToken, OtherOrigin);
        (await existing.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCors(await PreflightAsync(restarted, BrowserOrigin), BrowserOrigin);
        var pending = CreateAuthorization(primary);
        string pendingCode = await GetCodeAsync(restarted, pending);
        (await operatorClient.DeleteAsync($"{Registrations}/{primary.ClientId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await existing.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(restarted, primary.ClientId, tokens.RefreshToken, BrowserOrigin)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ExchangeAsync(restarted, primary, pending, pendingCode, BrowserOrigin)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await restarted.GetAsync(CreateAuthorization(primary).Url)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PreflightAsync(restarted, BrowserOrigin)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        (await survivingApp.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(restarted, other.ClientId, otherTokens.RefreshToken, OtherOrigin)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var firstParty = await FirstPartySignInAsync(restarted);
        using var portal = CreateConsumerClient(fixture, firstParty.AccessToken);
        (await portal.GetAsync("/api/me/settings")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetSessionsAsync(portal)).ShouldContain(session =>
            session.Device.StartsWith(primary.DisplayName, StringComparison.Ordinal) && session.Status == "Invalidated");
    }

    private static async Task AssertLibraryAndContentAsync(HttpClient client, DeliverySeed seed)
    {
        var catalog = (await client.GetFromJsonAsync<Page<ConsumerTitleCard>>("/api/catalog")).ShouldNotBeNull();
        catalog.Items.ShouldContain(item => item.Name == "Chrono Trigger");
        catalog.Items.ShouldNotContain(item => item.Name == "EarthBound");

        (await client.GetAsync($"/api/titles/{IdCoder.Encode(seed.OwnedTitleId)}/releases")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/titles/{IdCoder.Encode(seed.OtherLibraryTitleId)}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/releases/{IdCoder.Encode(seed.OtherLibraryReleaseId)}/manifest", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var response = await client.PostAsync($"/api/releases/{IdCoder.Encode(seed.OwnedReleaseId)}/manifest", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var manifest = (await response.Content.ReadFromJsonAsync<ConsumerReleaseManifest>(
            ConsumerDeliveryEndpointsTests.ConsumerJsonOptions)).ShouldNotBeNull();
        string url = manifest.Items.ShouldHaveSingleItem().ContentGrant.ShouldNotBeNull().DownloadUrl;
        using var download = await client.GetAsync(url);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(seed.ContentBytes);
    }

    private static async Task AssertRestrictedAsync(HttpClient consumer, HttpClient admin)
    {
        (await consumer.PutAsJsonAsync("/api/me/password", new ChangePasswordRequest(Password, "ChangedPassword123")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await consumer.GetAsync("/api/me/sessions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await consumer.DeleteAsync($"/api/me/sessions/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var req = new HttpRequestMessage(HttpMethod.Get, Registrations);
        req.Headers.Authorization = consumer.DefaultRequestHeaders.Authorization;
        (await admin.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpClient CreateConsumerClient(DeliveryFixture fixture, string token, string? origin = null)
    {
        var client = fixture.Factory.CreateClient(BrowserOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    private static async Task<IReadOnlyList<ConsumerSignInSession>> GetSessionsAsync(HttpClient portal) =>
        (await portal.GetFromJsonAsync<Page<ConsumerSignInSession>>("/api/me/sessions")).ShouldNotBeNull().Items;

    private static async Task<Tokens> FirstPartySignInAsync(HttpClient browser) =>
        ReadTokens(await OidcAuthorizationCodeFlow.RunAsync(browser, RomdOpenIddictClients.ConsumerSpa,
            "http://localhost:5174/auth/callback", Login, Password));

    private static async Task<Tokens> PairAsync(HttpClient browser, string appName, string deviceName, string scopes)
    {
        using var pairing = await browser.PostAsync("/connect/device", Form(new()
        {
            ["client_id"] = RomdOpenIddictClients.CommunityDevice,
            ["scope"] = scopes,
            ["app_name"] = appName,
            ["device_name"] = deviceName
        }));
        pairing.StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = await ReadObjectAsync(pairing);
        string code = ReadString(device, "user_code");
        string verification = $"/connect/verify?user_code={Uri.EscapeDataString(code)}";
        var page = await browser.GetStringAsync(verification);
        WebUtility.HtmlDecode(page).ShouldContain($"{appName} · {deviceName}");
        page.ShouldContain("Confirm that the device in front of you shows");
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = code,
            ["confirmed_code"] = code,
            ["login"] = Login,
            ["password"] = Password
        });
        (await browser.PostAsync("/connect/verify", form)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var token = await browser.PostAsync("/connect/token", Form(new()
        {
            ["client_id"] = RomdOpenIddictClients.CommunityDevice,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = ReadString(device, "device_code"),
            ["device_name"] = "Cannot relabel after approval"
        }));
        return await ReadTokensAsync(token, requireRefresh: scopes.Contains("offline_access", StringComparison.Ordinal));
    }

    private static RegisterConsumerWebAppRequest Registration(string origin) => new($"App {new Uri(origin).Host}", origin + "/callback", origin);

    private static async Task<ConsumerWebApp> RegisterAsync(HttpClient admin, string origin)
    {
        using var response = await admin.PostAsJsonAsync(Registrations, Registration(origin));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConsumerWebApp>(ConsumerDeliveryEndpointsTests.ConsumerJsonOptions)).ShouldNotBeNull();
    }

    private static async Task WaitForRegistrationsAsync(HttpClient admin, params string[] ids)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        do
        {
            var registrations = await admin.GetFromJsonAsync<ConsumerWebApp[]>(Registrations,
                ConsumerDeliveryEndpointsTests.ConsumerJsonOptions, timeout.Token);
            if (ids.All(id => registrations?.Any(app => app.ClientId == id && app.Status == ConsumerWebAppStatus.Active) == true))
                return;
        }
        while (await timer.WaitForNextTickAsync(timeout.Token));
    }

    private static Authorization CreateAuthorization(ConsumerWebApp app, string method = "S256", string? scopes = null)
    {
        string verifier = OidcAuthorizationCodeFlow.CreateCodeVerifier();
        string challenge = method == "S256" ? OidcAuthorizationCodeFlow.CreateCodeChallenge(verifier) : verifier;
        return new($"/connect/authorize?client_id={app.ClientId}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(app.CallbackUri)}&code_challenge={challenge}&code_challenge_method={method}" +
            $"&scope={Uri.EscapeDataString(scopes ?? "openid " + AllConsumerScopes)}&state=code-test", verifier);
    }

    private static async Task<string> GetCodeAsync(HttpClient browser, Authorization authorization)
    {
        using var initial = await browser.GetAsync(authorization.Url);
        initial.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        if (initial.Headers.Location?.OriginalString.StartsWith("/connect/login", StringComparison.Ordinal) != true)
        {
            string existing = OidcAuthorizationCodeFlow.ParseQueryValue(initial.Headers.Location!, "code");
            existing.ShouldNotBeNullOrWhiteSpace(initial.Headers.Location!.OriginalString);
            return existing;
        }

        using var form = await BrowserAuthenticationForm.CreateAsync(browser, initial.Headers.Location.OriginalString, new Dictionary<string, string>
        {
            ["returnUrl"] = authorization.Url,
            ["login"] = Login,
            ["password"] = Password
        });
        using var login = await browser.PostAsync("/connect/login", form);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var result = await browser.GetAsync(login.Headers.Location);
        result.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string code = OidcAuthorizationCodeFlow.ParseQueryValue(result.Headers.Location!, "code");
        code.ShouldNotBeNullOrWhiteSpace(result.Headers.Location!.OriginalString);
        return code;
    }

    private static Task<HttpResponseMessage> ExchangeAsync(HttpClient browser, ConsumerWebApp app,
        Authorization authorization, string code, string origin) =>
        TokenRequestAsync(browser, new()
        {
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = app.CallbackUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = authorization.Verifier,
            ["code"] = code
        }, origin);

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient browser, string clientId, string refreshToken, string? origin = null) =>
        TokenRequestAsync(browser, new()
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }, origin);

    private static async Task<HttpResponseMessage> TokenRequestAsync(HttpClient browser, Dictionary<string, string> values, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = Form(values) };
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return await browser.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient browser, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/connect/token");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return await browser.SendAsync(request);
    }

    private static void AssertCors(HttpResponseMessage response, string origin)
    {
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        response.Headers.Vary.ShouldContain("Origin");
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);
    private static async Task<Dictionary<string, JsonElement>> ReadObjectAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>()).ShouldNotBeNull();
    private static string ReadString(Dictionary<string, JsonElement> values, string name) => values[name].GetString().ShouldNotBeNull();
    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response, bool requireRefresh = true)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return ReadTokens(await ReadObjectAsync(response), requireRefresh);
    }

    private static Tokens ReadTokens(Dictionary<string, JsonElement> values, bool requireRefresh = true) =>
        new(ReadString(values, "access_token"), requireRefresh ? ReadString(values, "refresh_token") : "");
    private sealed record Authorization(string Url, string Verifier);
    private sealed record Tokens(string AccessToken, string RefreshToken);
}
