using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Consumer.Auth;
using Romd.Contracts.Consumer.Libraries;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence.Search;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerIdentityEndpointsTests
{
    private const string TestJwtAudience = "RomdConsumerEndpointTests";
    private const string TestJwtIssuer = "RomdConsumerEndpointTests";
    private const string TestJwtSecret = "consumer-endpoint-test-secret-at-least-32-characters";

    [Fact]
    public async Task DeviceAuthorization_ApprovedCode_IssuesRefreshableOpenIddictTokens()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.Factory.CreateClient();

        var deviceResponse = await client.PostAsync(
            "/connect/device",
            CreateFormContent(new Dictionary<string, string>
            {
                ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
                ["scope"] = "offline_access email profile roles"
            }));
        string deviceJson = await deviceResponse.Content.ReadAsStringAsync();
        deviceResponse.StatusCode.ShouldBe(HttpStatusCode.OK, deviceJson);
        var deviceBody = await ReadJsonObjectAsync(deviceResponse);
        string deviceCode = RequiredString(deviceBody, "device_code");
        string userCode = RequiredString(deviceBody, "user_code");

        var pendingResponse = await client.PostAsync(
            "/connect/token",
            CreateDeviceTokenRequest(deviceCode));
        var pendingBody = await ReadJsonObjectAsync(pendingResponse);

        var approvalResponse = await client.PostAsync(
            "/connect/verify",
            CreateFormContent(new Dictionary<string, string>
            {
                ["user_code"] = userCode,
                ["login"] = "player",
                ["password"] = "Password123"
            }));

        var tokenResponse = await client.PostAsync(
            "/connect/token",
            CreateDeviceTokenRequest(deviceCode));
        var tokenBody = await ReadJsonObjectAsync(tokenResponse);
        string accessToken = RequiredString(tokenBody, "access_token");
        string refreshToken = RequiredString(tokenBody, "refresh_token");

        using var authenticatedClient = fixture.Factory.CreateClient();
        authenticatedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResponse = await authenticatedClient.GetAsync("/api/me");
        var meBody = await meResponse.Content.ReadFromJsonAsync<CurrentUserDto>();

        var refreshResponse = await client.PostAsync(
            "/connect/token",
            CreateRefreshTokenRequest(refreshToken));
        var refreshBody = await ReadJsonObjectAsync(refreshResponse);
        string rotatedRefreshToken = OptionalString(refreshBody, "refresh_token") ?? refreshToken;

        var revocationResponse = await client.PostAsync(
            "/connect/revocation",
            CreateFormContent(new Dictionary<string, string>
            {
                ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
                ["token"] = rotatedRefreshToken
            }));

        var revokedRefreshResponse = await client.PostAsync(
            "/connect/token",
            CreateRefreshTokenRequest(rotatedRefreshToken));

        deviceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        pendingResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        RequiredString(pendingBody, "error").ShouldBe("authorization_pending");
        approvalResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        accessToken.ShouldNotBeNullOrWhiteSpace();
        refreshToken.ShouldNotBeNullOrWhiteSpace();
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        meBody.ShouldNotBeNull();
        meBody.Id.ShouldBe(user.Id);
        meBody.Email.ShouldBe("player@localhost");
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        RequiredString(refreshBody, "access_token").ShouldNotBeNullOrWhiteSpace();
        revocationResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        revokedRefreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuthorizationCode_Pkce_IssuesUsableOidcTokens()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");

        using var browser = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        string codeVerifier = CreateCodeVerifier();
        const string redirectUri = "http://localhost:5174/auth/callback";
        string authorizeUrl =
            "/connect/authorize?client_id=" + RomdOpenIddictClients.ConsumerSpa +
            "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email roles offline_access") +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&code_challenge=" + CreateCodeChallenge(codeVerifier) +
            "&code_challenge_method=S256&state=xyz";

        // Unauthenticated authorize redirects to the interactive login page.
        var challenge = await browser.GetAsync(authorizeUrl);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        challenge.Headers.Location!.OriginalString.ShouldStartWith("/connect/login");

        // Submitting credentials sets the login cookie and redirects back to the authorize request.
        var login = await browser.PostAsync(
            "/connect/login",
            CreateFormContent(new Dictionary<string, string>
            {
                ["returnUrl"] = authorizeUrl,
                ["login"] = "player",
                ["password"] = "Password123"
            }));
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // The authenticated authorize request issues the code via a redirect to the callback.
        var codeRedirect = await browser.GetAsync(login.Headers.Location);
        codeRedirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        codeRedirect.Headers.Location!.AbsoluteUri.ShouldStartWith(redirectUri);
        string code = ParseQueryValue(codeRedirect.Headers.Location!, "code");
        code.ShouldNotBeNullOrWhiteSpace();

        var tokenResponse = await browser.PostAsync(
            "/connect/token",
            CreateFormContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = RomdOpenIddictClients.ConsumerSpa,
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
                ["code_verifier"] = codeVerifier
            }));
        var tokenBody = await ReadJsonObjectAsync(tokenResponse);
        string accessToken = RequiredString(tokenBody, "access_token");

        using var authenticated = fixture.Factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResponse = await authenticated.GetAsync("/api/me");
        var meBody = await meResponse.Content.ReadFromJsonAsync<CurrentUserDto>();

        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        accessToken.ShouldNotBeNullOrWhiteSpace();
        // OIDC client: an id_token carrying sub must be returned, not just an access token.
        SubjectFromIdToken(RequiredString(tokenBody, "id_token")).ShouldBe(user.Id.ToString());
        OptionalString(tokenBody, "refresh_token").ShouldNotBeNull();
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        meBody.ShouldNotBeNull();
        meBody.Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Authorize_ClientNotBoundToHost_IsRejected()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var browser = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        string codeVerifier = CreateCodeVerifier();
        // romd-admin-spa is bound to the admin host; the consumer host must refuse it.
        const string adminRedirectUri = "http://localhost:5137/auth/callback";
        string authorizeUrl =
            "/connect/authorize?client_id=" + RomdOpenIddictClients.AdminSpa +
            "&response_type=code&scope=openid" +
            "&redirect_uri=" + Uri.EscapeDataString(adminRedirectUri) +
            "&code_challenge=" + CreateCodeChallenge(codeVerifier) +
            "&code_challenge_method=S256&state=xyz";

        var response = await browser.GetAsync(authorizeUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        ParseQueryValue(response.Headers.Location!, "code").ShouldBeNullOrEmpty();
        ParseQueryValue(response.Headers.Location!, "error").ShouldBe("unauthorized_client");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordChange_RevokesRealConsumerAndConsoleAccessAndRefresh(bool console)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var browser = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = true });
        Dictionary<string, JsonElement> issued;
        string clientId = console ? RomdOpenIddictApplicationSeeder.ConsoleClientId : RomdOpenIddictClients.ConsumerSpa;
        if (console)
        {
            var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
            { ["client_id"] = clientId, ["scope"] = "offline_access email profile roles" }));
            var deviceBody = await ReadJsonObjectAsync(device);
            var approval = await browser.PostAsync("/connect/verify", CreateFormContent(new Dictionary<string, string>
            { ["user_code"] = RequiredString(deviceBody, "user_code"), ["login"] = "player", ["password"] = "Password123" }));
            approval.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            var response = await browser.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(deviceBody, "device_code")));
            issued = await ReadJsonObjectAsync(response);
        }
        else issued = await OidcAuthorizationCodeFlow.RunAsync(browser, clientId,
            "http://localhost:5174/auth/callback", "player", "Password123");
        using var session = fixture.Factory.CreateClient();
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequiredString(issued, "access_token"));
        (await session.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var changed = await session.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest("Password123", "Password456"));
        changed.IsSuccessStatusCode.ShouldBeTrue(await changed.Content.ReadAsStringAsync());
        (await session.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refresh = await browser.PostAsync("/connect/token", CreateFormContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = RequiredString(issued, "refresh_token") }));
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_AuthenticatedUser_ChangesPassword()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.CreateAuthenticatedClient(user.Id, "player@localhost");

        var response = await client.PostAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest("Password123", "Password456"));
        var oldPasswordLogin = await SubmitConnectLoginAsync(fixture, "player", "Password123");
        var newPasswordLogin = await SubmitConnectLoginAsync(fixture, "player", "Password456");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        oldPasswordLogin.ShouldBe(HttpStatusCode.Unauthorized);
        newPasswordLogin.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ChangePassword_UsesConfiguredIdentityPasswordPolicy()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
        {
            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequiredLength = 12;
            });
        });
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.CreateAuthenticatedClient(user.Id, "player@localhost");

        var response = await client.PostAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest("Password123", "Pass1234"));
        var rejectedPasswordLogin = await SubmitConnectLoginAsync(fixture, "player", "Pass1234");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        rejectedPasswordLogin.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_UnauthenticatedRequest_ReturnsUnauthorized()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest("Password123", "Password456"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settings_AuthenticatedUser_ReadsAndUpdatesSettings()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.CreateAuthenticatedClient(user.Id, "player@localhost");

        var readDefaultResponse = await client.GetAsync("/api/account/settings");
        var defaultSettings = await readDefaultResponse.Content.ReadFromJsonAsync<ConsumerUserSettingsDto>();
        var preference = new ConsumerReleasePreferenceDto(
            [IdCoder.Encode(42)],
            [IdCoder.Encode(7)],
            "newestFirst");
        var updateResponse = await client.PutAsJsonAsync(
            "/api/account/settings",
            new UpdateConsumerUserSettingsRequest("dark", preference));
        var updatedSettings = await updateResponse.Content.ReadFromJsonAsync<ConsumerUserSettingsDto>();
        var readUpdatedResponse = await client.GetAsync("/api/account/settings");
        var readUpdatedSettings = await readUpdatedResponse.Content.ReadFromJsonAsync<ConsumerUserSettingsDto>();

        readDefaultResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        defaultSettings.ShouldNotBeNull();
        defaultSettings.Theme.ShouldBe("system");
        defaultSettings.ReleasePreference.PreferredRegionIds.ShouldBeEmpty();
        defaultSettings.ReleasePreference.PreferredLanguageIds.ShouldBeEmpty();
        defaultSettings.ReleasePreference.RevisionStrategy.ShouldBe("none");
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        updatedSettings.ShouldNotBeNull();
        updatedSettings.Theme.ShouldBe("dark");
        updatedSettings.ReleasePreference.PreferredRegionIds.ShouldBe(preference.PreferredRegionIds);
        updatedSettings.ReleasePreference.PreferredLanguageIds.ShouldBe(preference.PreferredLanguageIds);
        updatedSettings.ReleasePreference.RevisionStrategy.ShouldBe(preference.RevisionStrategy);
        readUpdatedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        readUpdatedSettings.ShouldNotBeNull();
        readUpdatedSettings.Theme.ShouldBe("dark");
        readUpdatedSettings.ReleasePreference.PreferredRegionIds.ShouldBe(preference.PreferredRegionIds);
        readUpdatedSettings.ReleasePreference.PreferredLanguageIds.ShouldBe(preference.PreferredLanguageIds);
        readUpdatedSettings.ReleasePreference.RevisionStrategy.ShouldBe(preference.RevisionStrategy);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("put")]
    public async Task Settings_UnauthenticatedRequest_ReturnsUnauthorized(string method)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = method switch
        {
            "get" => await client.GetAsync("/api/account/settings"),
            _ => await client.PutAsJsonAsync(
                "/api/account/settings",
                new UpdateConsumerUserSettingsRequest("dark"))
        };

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_AuthenticatedUser_ReturnsCurrentUserDto()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var userId = Guid.NewGuid();
        using var client = fixture.CreateAuthenticatedClient(userId, "player@localhost");

        var response = await client.GetAsync("/api/me");
        var body = await response.Content.ReadFromJsonAsync<CurrentUserDto>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Id.ShouldBe(userId);
        body.Email.ShouldBe("player@localhost");
        body.Roles.ShouldBe(["User"]);
    }

    [Fact]
    public async Task GetMeLibrary_AuthenticatedUserWithLibrary_ReturnsLibraryContextDto()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        int libraryId = await fixture.CreateLibraryAsync("Living Room");
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost", libraryId);

        var response = await client.GetAsync("/api/me/library");
        var body = await response.Content.ReadFromJsonAsync<LibraryContextDto>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("Living Room");
        body.Counts.OwnedTitleCount.ShouldBe(0);
        body.Counts.AvailableTitleCount.ShouldBe(0);
        body.Counts.CollectionCount.ShouldBe(0);
        body.Platforms.ShouldBeEmpty();
        body.Genres.ShouldBeEmpty();
        body.FeaturedCollections.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/me/library")]
    public async Task ConsumerIdentityEndpoints_UnauthenticatedRequest_ReturnsUnauthorized(string path)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMeLibrary_AuthenticatedUserWithoutLibrary_ReturnsNotFound()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "player@localhost");

        var response = await client.GetAsync("/api/me/library");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static FormUrlEncodedContent CreateDeviceTokenRequest(string deviceCode) =>
        CreateFormContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
            ["device_code"] = deviceCode
        });

    private static FormUrlEncodedContent CreateRefreshTokenRequest(string refreshToken) =>
        CreateFormContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = RomdOpenIddictApplicationSeeder.ConsoleClientId,
            ["refresh_token"] = refreshToken
        });

    private static FormUrlEncodedContent CreateFormContent(IReadOnlyDictionary<string, string> values) =>
        new(values);

    // Verifies a password by attempting the interactive server login: success redirects (302), bad
    // credentials re-render the login page with 401. AllowAutoRedirect is off so the 302 is observable.
    private static async Task<HttpStatusCode> SubmitConnectLoginAsync(
        ConsumerHostFixture fixture,
        string login,
        string password)
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var response = await client.PostAsync(
            "/connect/login",
            CreateFormContent(new Dictionary<string, string>
            {
                ["login"] = login,
                ["password"] = password
            }));

        return response.StatusCode;
    }

    private static async Task<Dictionary<string, JsonElement>> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        OptionalString(values, name) ?? throw new InvalidOperationException($"Response did not include '{name}'.");

    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string CreateCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ParseQueryValue(Uri uri, string key)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key)
            {
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string? SubjectFromIdToken(string idToken) =>
        new JwtSecurityToken(idToken).Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;

    private sealed class ConsumerHostFixture : IAsyncDisposable
    {
        private readonly string _tempDataDirectory;
        private readonly PostgreSqlTestDatabase _database;

        private ConsumerHostFixture(
            WebApplicationFactory<Romd.Consumer.Host.Program> factory,
            PostgreSqlTestDatabase database,
            string tempDataDirectory)
        {
            Factory = factory;
            _database = database;
            _tempDataDirectory = tempDataDirectory;
        }

        public WebApplicationFactory<Romd.Consumer.Host.Program> Factory { get; }

        public static async Task<ConsumerHostFixture> CreateAsync(
            Action<IServiceCollection>? configureServices = null)
        {
            var database = PostgreSqlTestDatabase.Create();

            string tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-consumer-endpoints-{Guid.NewGuid():N}");
            var webRoot = Path.Combine(tempDataDirectory, "wwwroot");
            Directory.CreateDirectory(webRoot);
            OpenIddictSigningKey.EnsureCreated(tempDataDirectory);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");

            var factory = new WebApplicationFactory<Romd.Consumer.Host.Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(AppContext.BaseDirectory);
                    builder.UseWebRoot(webRoot);
                    builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                    builder.UseSetting("Romd:DataDirectory", tempDataDirectory);
                    builder.UseSetting(
                        $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                        database.ConnectionString);
                    builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                    builder.UseSetting("Romd:JwtIssuer", TestJwtIssuer);
                    builder.UseSetting("Romd:JwtAudience", "RomdDefaultConsumerEndpointTests");
                    builder.UseSetting("Romd:ConsumerHost:JwtAudience", TestJwtAudience);

                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.RemoveAll<DbContextOptions<RomdDbContext>>();

                        services.AddDbContext<RomdDbContext>((sp, options) =>
                        {
                            PostgreSqlConfiguration.Configure(options, database.ConnectionString);
                            options.UseOpenIddict();
                            options.EnableDetailedErrors();
                            options.AddInterceptors(
                                sp.GetRequiredService<AuditInterceptor>(),
                                new SearchDocumentInterceptor());
                        });

                        services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

                        configureServices?.Invoke(services);
                    });
                });

            var fixture = new ConsumerHostFixture(factory, database, tempDataDirectory);
            await fixture.MigrateAsync();
            await fixture.SeedOpenIddictAsync();

            return fixture;
        }

        public HttpClient CreateAuthenticatedClient(Guid userId, string email, int? libraryId = null)
        {
            using var context = CreateSetupDbContext();
            if (!context.Users.Any(user => user.Id == userId))
            {
                string userName = $"consumer-{userId:N}";
                context.Users.Add(new RomdUser
                {
                    Id = userId,
                    UserName = userName,
                    NormalizedUserName = userName.ToUpperInvariant(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    LibraryId = libraryId,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                context.SaveChanges();
            }

            var client = Factory.CreateClient();
            client.WithTestUser(userId, email, [RomdRoleType.User], libraryId);

            return client;
        }

        public async Task<int> CreateLibraryAsync(string name)
        {
            await using var context = CreateSetupDbContext();
            var library = Library.CreateNew(name, new LibraryConfiguration());
            var entity = LibraryEntity.FromDomain(library);
            entity.NeedsMaterialization = false;
            context.Libraries.Add(entity);
            await context.SaveChangesAsync();

            return entity.Id;
        }

        public async Task<RomdUser> CreateConsumerUserAsync(
            string userName,
            string email,
            string password,
            Action<RomdUser>? configure = null)
        {
            await using var context = CreateSetupDbContext();
            var passwordHasher = new PasswordHasher<RomdUser>();
            var user = RomdUser.Create(userName, email);
            user.NormalizedUserName = userName.ToUpperInvariant();
            user.NormalizedEmail = email.ToUpperInvariant();
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            configure?.Invoke(user);

            context.Users.Add(user);
            await context.SaveChangesAsync();

            return user;
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await _database.DisposeAsync();

            if (Directory.Exists(_tempDataDirectory))
            {
                Directory.Delete(_tempDataDirectory, true);
            }
        }

        private async Task MigrateAsync()
        {
            await using var context = CreateSetupDbContext();
            await context.Database.MigrateAsync();
        }

        private async Task SeedOpenIddictAsync()
        {
            // The worker seeds the console client through guard-free admin persistence in production;
            // the consumer write guard blocks application writes. Seed through a guard-free provider here.
            var services = new ServiceCollection();
            services.AddDbContext<RomdDbContext>(options =>
            {
                options.UseNpgsql(_database.ConnectionString);
                options.UseOpenIddict();
            });
            services.AddOpenIddict()
                .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<RomdDbContext>());

            await using var provider = services.BuildServiceProvider();
            var seeder = new RomdOpenIddictApplicationSeeder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().Build());
            await seeder.StartAsync(CancellationToken.None);
        }

        private RomdDbContext CreateSetupDbContext()
        {
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(new SearchDocumentInterceptor())
                .UseOpenIddict()
                .EnableDetailedErrors()
                .Options;

            return new RomdDbContext(options);
        }
    }
}
