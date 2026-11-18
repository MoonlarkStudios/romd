using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class SignInMessageEndpointsTests(IntegrationTestFixture fixture)
{
    private const string SettingsPath = "/api/server/sign-in-message";
    private const string LoginPath = "/connect/login";
    private const string MessageElement = """<p class="romd-auth-description romd-auth-message">""";

    [Fact]
    public async Task SignInMessage_AdminSavesAndClears_BothCredentialFormsReflectTheSamePlainText()
    {
        await using var consumerFactory = CreateConsumerHost();
        using var consumer = consumerFactory.CreateClient();
        using var browser = fixture.CreateClient();
        using var admin = fixture.CreateAuthenticatedClient();

        using var initial = await admin.GetAsync(SettingsPath);
        var blank = await AssertSettingsAsync(initial, string.Empty);
        foreach (var portal in new[] { browser, consumer })
            (await portal.GetStringAsync(LoginPath)).ShouldNotContain(MessageElement);

        const string message = "Welcome & enjoy!\nFor help, ask <the operator>.";
        using var saved = await ReplaceAsync(admin, $"  {message}  ", Tag(blank));
        var configured = await AssertSettingsAsync(saved, message);
        configured.Revision.ShouldNotBe(blank.Revision);
        using var stored = await admin.GetAsync(SettingsPath);
        (await AssertSettingsAsync(stored, message)).ShouldBe(configured);

        foreach (var portal in new[] { browser, consumer })
        {
            AssertRenderedMessage(await portal.GetStringAsync(LoginPath), message);

            using var form = await BrowserAuthenticationForm.CreateAsync(portal, LoginPath,
                new Dictionary<string, string>
                {
                    ["login"] = $"unknown-{Guid.NewGuid():N}@example.test",
                    ["password"] = "test-only",
                    ["returnUrl"] = "/"
                });
            using var failure = await portal.PostAsync(LoginPath, form);
            failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            string html = await failure.Content.ReadAsStringAsync();
            html.ShouldContain("Invalid ROMD credentials.");
            AssertRenderedMessage(html, message);
        }

        using var stale = await ReplaceAsync(admin, "Stale text", Tag(blank));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var invalid = await ReplaceAsync(admin, new string('x', ReplaceSignInMessageRequest.MaximumLength + 1), Tag(configured));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var cleared = await ReplaceAsync(admin, " \t\n ", Tag(configured));
        await AssertSettingsAsync(cleared, string.Empty);
        foreach (var portal in new[] { browser, consumer })
            (await portal.GetStringAsync(LoginPath)).ShouldNotContain(MessageElement);
    }

    [Fact]
    public async Task SignInMessage_NonAdminRequests_CannotReadOrReplaceTheSettingsResource()
    {
        using var browser = fixture.CreateClient();
        using var contributor = fixture.CreateClient()
            .WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Contributor]);
        string revision = $"\"{Guid.Empty}\"";

        using var anonymousRead = await browser.GetAsync(SettingsPath);
        using var anonymousWrite = await ReplaceAsync(browser, "Notice", revision);
        anonymousRead.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymousWrite.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var contributorRead = await contributor.GetAsync(SettingsPath);
        using var contributorWrite = await ReplaceAsync(contributor, "Notice", revision);
        contributorRead.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        contributorWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateConsumerHost()
    {
        var config = fixture.Services.GetRequiredService<IConfiguration>();
        return new WebApplicationFactory<Romd.Consumer.Host.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseWebRoot(Path.Combine(fixture.DataDirectoryPath, "wwwroot"));
            builder.UseSetting("Romd:DataDirectory", fixture.DataDirectoryPath);
            builder.UseSetting("Romd:JwtSecret", config["Romd:JwtSecret"]);
            builder.UseSetting($"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                PostgreSqlConfiguration.GetRequiredConnectionString(config, PostgreSqlConfiguration.RuntimeConnectionName));
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
    }

    private static async Task<HttpResponseMessage> ReplaceAsync(HttpClient client, string message, string tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, SettingsPath)
        {
            Content = JsonContent.Create(new ReplaceSignInMessageRequest { Message = message })
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    private static async Task<SignInMessageSettings> AssertSettingsAsync(HttpResponseMessage response, string message)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = (await response.Content.ReadFromJsonAsync<SignInMessageSettings>()).ShouldNotBeNull();
        settings.Message.ShouldBe(message);
        response.Headers.ETag?.Tag.ShouldBe(Tag(settings));
        return settings;
    }

    private static string Tag(SignInMessageSettings settings) => $"\"{settings.Revision}\"";

    private static void AssertRenderedMessage(string html, string message)
    {
        html.ShouldContain($"{MessageElement}{WebUtility.HtmlEncode(message)}</p>");
        html.ShouldNotContain("<the operator>");
        html.Split(MessageElement).Length.ShouldBe(2);
    }
}
