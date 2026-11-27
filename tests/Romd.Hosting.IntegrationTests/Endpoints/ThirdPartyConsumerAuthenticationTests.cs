using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Activity;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Management.ConsumerApps;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Identity;
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
    public async Task PairConsole_LegacyClientRequest_CanSyncPlaySessionsWithNoHistoryOrAccountAccess()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.SeedCrossSurfaceDataAsync(seed);
        await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);
        using var browser = fixture.Factory.CreateClient(BrowserOptions);

        const string clientId = RomdOpenIddictClients.Console;
        const string legacyScopes = "offline_access email profile roles";
        var console = await PairAsync(browser, "Console", "Living room", legacyScopes, clientId,
            "Sync play sessions to your ROMD account");
        using var device = CreateConsumerClient(fixture, console.AccessToken);
        await AssertLibraryAndContentAsync(device, seed);
        (await device.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        string history = "/api/me/activity/play-sessions";
        string session = $"{history}/{Guid.NewGuid()}";
        var play = new UpsertPlaySessionRequest
        {
            ClientId = "living-room",
            TitleId = IdCoder.Encode(seed.OwnedTitleId),
            ReleaseId = IdCoder.Encode(seed.OwnedReleaseId),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        using var saved = await device.PutAsJsonAsync(session, play);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await device.GetAsync(history)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.GetAsync(session)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.DeleteAsync(history)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.GetAsync("/api/me/settings")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        const string playLater = "/api/me/play-later";
        string titleId = IdCoder.Encode(seed.OwnedTitleId);
        (await device.GetAsync(playLater)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.GetAsync($"{playLater}/membership?titleIds={titleId}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.PutAsync($"{playLater}/{titleId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await device.DeleteAsync($"{playLater}/{titleId}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var refresh = await RefreshAsync(browser, clientId, console.RefreshToken);
        var refreshed = await ReadTokensAsync(refresh, requireRefresh: true);
        using var refreshedDevice = CreateConsumerClient(fixture, refreshed.AccessToken);
        (await refreshedDevice.PutAsJsonAsync(session, play)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refreshedDevice.DeleteAsync(history)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var portalTokens = await FirstPartySignInAsync(browser);
        using var portal = CreateConsumerClient(fixture, portalTokens.AccessToken);
        var historyPage = (await portal.GetFromJsonAsync<Page<PlaySession>>(history)).ShouldNotBeNull();
        historyPage.Items.ShouldHaveSingleItem().ClientId.ShouldBe("living-room");
        (await portal.PutAsync($"{playLater}/{titleId}", content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ConsoleTokens_FromBeforeScopedDeviceGrants_KeepCompatibleAccessAndStayDownscopedAfterRefresh()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);

        // Issue the previous token shape using the same issuer keys and stored account session.
        // The verification host itself has no test handler that modifies grants.
        using var legacyFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddOpenIddict().AddServer(options => options
                .AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler.UseInlineHandler(context =>
                {
                    if (context.EndpointType == OpenIddictServerEndpointType.Token)
                    {
                        var principal = context.Principal!;
                        principal.SetClaim(DeviceGrantPermissions.ConsentClaim, (string?)null);
                        principal.SetScopes(["offline_access", "email", "profile", "roles"]);
                    }

                    return ValueTask.CompletedTask;
                }).SetOrder(int.MinValue + 1000)))));
        using var legacyBrowser = legacyFactory.CreateClient(BrowserOptions);
        const string clientId = RomdOpenIddictClients.Console;
        var legacy = await PairAsync(legacyBrowser, "Console", "Legacy", "offline_access email profile roles", clientId);
        using var legacyDevice = CreateConsumerClient(fixture, legacy.AccessToken);
        (await legacyDevice.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await legacyDevice.DeleteAsync("/api/me/activity/play-sessions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var browser = fixture.Factory.CreateClient(BrowserOptions);
        using var refresh = await RefreshAsync(browser, clientId, legacy.RefreshToken);
        var refreshed = await ReadTokensAsync(refresh);
        using var refreshedDevice = CreateConsumerClient(fixture, refreshed.AccessToken);
        (await refreshedDevice.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var downgrade = await RefreshAsync(browser, clientId, refreshed.RefreshToken, scopes: "offline_access");
        var reduced = await ReadTokensAsync(downgrade);
        using var secondRefresh = await RefreshAsync(browser, clientId, reduced.RefreshToken);
        var stillReduced = await ReadTokensAsync(secondRefresh);
        foreach (string accessToken in new[] { reduced.AccessToken, stillReduced.AccessToken })
        {
            using var device = CreateConsumerClient(fixture, accessToken);
            (await device.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task DevelopmentMount_RespectsApprovedMountPermissionAndRejectsCommunityDeviceGrant()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);
        using var host = fixture.Factory.WithWebHostBuilder(builder => builder
            .UseSetting("Romd:Vfs:Enabled", "true")
            .UseSetting("Romd:Vfs:AllowDevelopmentBearer", "true"));
        using var browser = host.CreateClient(BrowserOptions);
        var mount = await PairAsync(browser, "Mount", "First", "offline_access email profile roles", RomdOpenIddictClients.Mount,
            "Mount your assigned library as a read-only drive");
        var community = await PairAsync(browser, "Community", "Second", AllConsumerScopes);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/vfs/library/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mount.AccessToken);
        (await browser.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var denied = new HttpRequestMessage(HttpMethod.Options, "/vfs/library/");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", community.AccessToken);
        (await browser.SendAsync(denied)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(RomdOpenIddictClients.Console)]
    [InlineData(RomdOpenIddictClients.Mount)]
    public async Task PairDevice_BuiltInIdWithLimitedConsent_DoesNotGainOtherPermissions(string clientId)
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);
        using var browser = fixture.Factory.CreateClient(BrowserOptions);
        var tokens = await PairAsync(browser, "Example app", "Device", "offline_access romd:discovery", clientId,
            "Identify your ROMD account");

        using var refresh = await RefreshAsync(browser, clientId, tokens.RefreshToken);
        var refreshed = await ReadTokensAsync(refresh, requireRefresh: true);
        foreach (string accessToken in new[] { tokens.AccessToken, refreshed.AccessToken })
        {
            using var device = CreateConsumerClient(fixture, accessToken);
            (await device.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await device.GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await device.GetAsync("/api/me/activity/play-sessions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await device.DeleteAsync("/api/me/activity/play-sessions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using var request = await device.PutAsJsonAsync($"/api/me/activity/play-sessions/{Guid.NewGuid()}", new { });
            request.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task PairDevice_ConsentForOneCode_CannotBeAppliedToAnotherCode()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        using var browser = fixture.Factory.CreateClient(BrowserOptions);
        var first = await RequestDeviceCodeAsync(browser, RomdOpenIddictClients.Console,
            "openid email offline_access romd:discovery", "Example", "First");
        using var verification = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(ReadString(first, "user_code"))}");
        AssertUnframed(verification);
        var preview = await verification.Content.ReadAsStringAsync();
        preview.ShouldContain("Read your email address");
        preview.ShouldContain("Stay signed in on this device");
        preview.ShouldContain("Verify your ROMD identity");
        var second = await RequestDeviceCodeAsync(browser, RomdOpenIddictClients.Console, "romd:library", "Example", "Second");

        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = ReadString(first, "user_code"),
            ["login"] = Login,
            ["password"] = Password
        });
        var fields = QueryHelpers.ParseQuery(await form.ReadAsStringAsync())
            .ToDictionary(field => field.Key, field => field.Value.ToString(), StringComparer.Ordinal);
        fields["user_code"] = ReadString(second, "user_code");
        using var approval = await browser.PostAsync("/connect/verify", Form(fields));
        approval.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await approval.Content.ReadAsStringAsync();
        page.ShouldContain("Browse your assigned library");
        page.ShouldNotContain("Identify your ROMD account");
        page.ShouldNotContain("Read your email address");
        using var pending = await ExchangeDeviceCodeAsync(browser, RomdOpenIddictClients.Console, second);
        ReadString(await ReadObjectAsync(pending), "error").ShouldBe("authorization_pending");
    }

    [Fact]
    public async Task ListRegistrations_FailedProvisioningIsVisibleAndIndependent_PagesByStableCursor()
    {
        await using var fixture = await DeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var user = await fixture.CreatePasswordUserAsync(Login, "player@example.com", Password, seed.LibraryId);
        using var adminHost = fixture.CreateAdminHost();
        using var admin = adminHost.CreateClient();
        admin.WithTestUser(user.Id, user.Email!, [RomdRoleType.Admin]);

        await using var workers = fixture.CreateWorkerIdentityServices();
        const string failedId = "romd-web-00000000000000000000000000000000";
        string displayName = new('界', 120);
        string retryOrigin = "https://failed.example";
        await using (var scope = workers.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ConsumerWebAppRegistrationStore>();
            store.StageRegistration(new(failedId, displayName, "unparseable uri", retryOrigin, null, null, null));
            await scope.ServiceProvider.GetRequiredService<RomdDbContext>().SaveChangesAsync();
        }

        using var created = await admin.PostAsJsonAsync(Registrations, Registration(BrowserOrigin) with { DisplayName = displayName });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var valid = (await created.Content.ReadFromJsonAsync<ConsumerWebApp>(ConsumerDeliveryEndpointsTests.ConsumerJsonOptions)).ShouldNotBeNull();
        using var worker = new ConsumerWebAppProvisioningWorker(workers.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, workers.GetRequiredService<ILogger<ConsumerWebAppProvisioningWorker>>());
        await worker.StartAsync(default);
        await WaitForRegistrationsAsync(admin, valid.ClientId);
        await worker.StopAsync(default);

        var firstPage = await GetRegistrationPageAsync(admin, $"{Registrations}?limit=1");
        firstPage.HasNextPage.ShouldBeTrue();
        firstPage.NextCursor.ShouldNotBeNullOrWhiteSpace();
        var failed = firstPage.Items.ShouldHaveSingleItem();
        failed.ClientId.ShouldBe(failedId);
        failed.Status.ShouldBe(ConsumerWebAppStatus.ProvisioningFailed);
        var nextPage = await GetRegistrationPageAsync(admin, $"{Registrations}?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor)}");
        nextPage.HasNextPage.ShouldBeFalse();
        nextPage.Items.ShouldHaveSingleItem().ClientId.ShouldBe(valid.ClientId);
        (await admin.GetAsync($"{Registrations}?cursor=invalid")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"{Registrations}?limit=101")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var consumer = fixture.Factory.CreateClient(BrowserOptions);
        (await PreflightAsync(consumer, retryOrigin)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        (await admin.DeleteAsync($"{Registrations}/{failedId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"{Registrations}/{failedId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
        attempt = attempt with { Url = attempt.Url.Replace("/connect/authorize?", "/CONNECT/AUTHORIZE/?", StringComparison.Ordinal) };
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

    private static async Task<Tokens> PairAsync(HttpClient browser, string appName, string deviceName, string scopes,
        string clientId = RomdOpenIddictClients.CommunityDevice, string? requiredPermission = null)
    {
        var device = await RequestDeviceCodeAsync(browser, clientId, scopes, appName, deviceName);
        string code = ReadString(device, "user_code");
        string verification = $"/connect/verify?user_code={Uri.EscapeDataString(code)}";
        var page = await browser.GetStringAsync(verification);
        WebUtility.HtmlDecode(page).ShouldContain($"{appName} · {deviceName}");
        page.ShouldContain("Confirm that the device in front of you shows");
        if (requiredPermission is not null)
            page.ShouldContain(requiredPermission);

        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = code,
            ["login"] = Login,
            ["password"] = Password
        });
        (await browser.PostAsync("/connect/verify", form)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var token = await ExchangeDeviceCodeAsync(browser, clientId, device);
        return await ReadTokensAsync(token, requireRefresh: scopes.Contains("offline_access", StringComparison.Ordinal));
    }

    private static async Task<Dictionary<string, JsonElement>> RequestDeviceCodeAsync(
        HttpClient browser, string clientId, string scopes, string appName, string deviceName)
    {
        using var pairing = await browser.PostAsync("/connect/device", Form(new()
        {
            ["client_id"] = clientId,
            ["scope"] = scopes,
            ["app_name"] = appName,
            ["device_name"] = deviceName
        }));
        pairing.StatusCode.ShouldBe(HttpStatusCode.OK, await pairing.Content.ReadAsStringAsync());
        return await ReadObjectAsync(pairing);
    }

    private static Task<HttpResponseMessage> ExchangeDeviceCodeAsync(HttpClient browser, string clientId,
        Dictionary<string, JsonElement> device) =>
        browser.PostAsync("/connect/token", Form(new()
        {
            ["client_id"] = clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = ReadString(device, "device_code"),
            ["device_name"] = "Cannot relabel after approval"
        }));

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
            var registrations = await GetRegistrationPageAsync(admin, Registrations, timeout.Token);
            if (ids.All(id => registrations.Items.Any(app => app.ClientId == id && app.Status == ConsumerWebAppStatus.Active)))
                return;
        }
        while (await timer.WaitForNextTickAsync(timeout.Token));
    }

    private static async Task<Page<ConsumerWebApp>> GetRegistrationPageAsync(HttpClient admin, string path, CancellationToken ct = default) =>
        (await admin.GetFromJsonAsync<Page<ConsumerWebApp>>(path, ConsumerDeliveryEndpointsTests.ConsumerJsonOptions, ct)).ShouldNotBeNull();

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

        using var loginPage = await browser.GetAsync(initial.Headers.Location);
        AssertUnframed(loginPage);
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

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient browser, string clientId, string refreshToken,
        string? origin = null, string? scopes = null)
    {
        Dictionary<string, string> values = new()
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
        if (scopes is not null)
            values["scope"] = scopes;
        return TokenRequestAsync(browser, values, origin);
    }

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

    private static void AssertUnframed(HttpResponseMessage response)
    {
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["frame-ancestors 'none'"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
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
