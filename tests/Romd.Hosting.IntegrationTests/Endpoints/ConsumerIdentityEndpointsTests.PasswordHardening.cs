using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Romd.Contracts.Consumer.Account;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePassword_OverlongInput_PreservesConsumerCredentials(bool current)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.CreateAuthenticatedClient(user.Id, "player@localhost");
        string overlong = "Aa1" + new string('x', 4094);

        using var response = await client.PutAsJsonAsync("/api/me/password",
            new ChangePasswordRequest(current ? overlong : "Password123", current ? "NewPassword123" : overlong));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SubmitConnectLoginAsync(fixture, "player", "Password123")).ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ChangePassword_MaximumLengthUnicodePassword_CanApproveDevice()
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync();
        var user = await fixture.CreateConsumerUserAsync("player", "player@localhost", "Password123");
        using var client = fixture.CreateAuthenticatedClient(user.Id, "player@localhost");
        string password = "Aa1" + new string('\u4e00', 4093);
        using var changed = await client.PutAsJsonAsync("/api/me/password", new ChangePasswordRequest("Password123", password));
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var browser = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var device = await browser.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        {
            ["client_id"] = RomdOpenIddictClients.Console,
            ["scope"] = "offline_access email profile roles"
        }));
        var authorization = await ReadJsonObjectAsync(device);
        using var form = await BrowserAuthenticationForm.CreateAsync(browser, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = RequiredString(authorization, "user_code"),
            ["login"] = "player",
            ["password"] = password
        });

        using var approved = await browser.PostAsync("/connect/verify", form);
        approved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var tokens = await browser.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(authorization, "device_code")));
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
