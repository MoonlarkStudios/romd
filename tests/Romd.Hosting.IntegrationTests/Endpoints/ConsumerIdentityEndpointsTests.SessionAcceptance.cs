using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Theory]
    [InlineData(RomdOpenIddictClients.Console)]
    [InlineData(RomdOpenIddictClients.Mount)]
    public async Task DeviceAcceptance_CommitsBeforeIssuance_AndExchangeAndRefreshReuseAbsoluteSession(string clientId)
    {
        var probe = new AccountSessionIssuanceProbe();
        await using var fixture = await ConsumerHostFixture.CreateAsync(probe.Configure);
        string login = $"session-player-{Guid.NewGuid():N}";
        var user = await fixture.CreateConsumerUserAsync(login, login + "@localhost", "Password123");
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using var request = await client.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        { ["client_id"] = clientId, ["scope"] = "offline_access email profile roles" }));
        request.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await ReadJsonObjectAsync(request);
        using var missingAntiforgery = await client.PostAsync("/connect/verify", CreateFormContent(new Dictionary<string, string>
        { ["user_code"] = RequiredString(code, "user_code"), ["login"] = login, ["password"] = "Password123" }));
        missingAntiforgery.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        probe.Starts.ShouldBe(0);

        using var approved = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify",
            new Dictionary<string, string> { ["user_code"] = RequiredString(code, "user_code"), ["login"] = login, ["password"] = "Password123" }));

        // Browser approval redirects home; this client deliberately does not follow it.
        approved.StatusCode.ShouldBe(HttpStatusCode.Found);
        approved.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/");
        var session = probe.Accepted.ShouldHaveSingleItem();
        session.UserId.ShouldBe(user.Id);
        session.ClientId.ShouldBe(clientId);
        session.Device.ShouldBe(clientId == RomdOpenIddictClients.Mount ? "ROMD Mount" : "Console");
        probe.IssuanceAttempts.ShouldContain(item => item.SessionId == session.Id
            && item.Scheme == OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        using var tokens = await client.PostAsync("/connect/token", CreateFormContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = RequiredString(code, "device_code"),
            ["device_name"] = "  Console\n on Linux  "
        }));
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await ReadJsonObjectAsync(tokens);
        using var refresh = await client.PostAsync("/connect/token", CreateRefreshTokenRequest(RequiredString(issued, "refresh_token"), clientId));
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        probe.Starts.ShouldBe(1);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<RomdDbContext>().AccountSessions.Where(row => row.UserId == user.Id).ToListAsync();
        var row = rows.ShouldHaveSingleItem();
        row.Id.ShouldBe(session.Id);
        row.Device.ShouldBe("Console on Linux");
        row.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(session.CreatedAt.ToUnixTimeMilliseconds()));
        row.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAt.ToUnixTimeMilliseconds()));
    }

    [Theory]
    [InlineData(SessionAcceptanceFault.BeforeAcceptance)]
    [InlineData(SessionAcceptanceFault.ProtocolIssuance)]
    public async Task DeviceAcceptance_FaultBeforeOrAfterCommit_DoesNotPretendProtocolApprovalWasAtomic(SessionAcceptanceFault fault)
    {
        var probe = new AccountSessionIssuanceProbe { Fault = fault };
        await using var fixture = await ConsumerHostFixture.CreateAsync(probe.Configure);
        string login = $"session-fault-{Guid.NewGuid():N}";
        var user = await fixture.CreateConsumerUserAsync(login, login + "@localhost", "Password123");
        using var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using var request = await client.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
        { ["client_id"] = RomdOpenIddictClients.Console, ["scope"] = "offline_access email profile roles" }));
        var code = await ReadJsonObjectAsync(request);

        using var failed = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify",
            new Dictionary<string, string> { ["user_code"] = RequiredString(code, "user_code"), ["login"] = login, ["password"] = "Password123" }));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        probe.Starts.ShouldBe(1);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int expected = fault == SessionAcceptanceFault.BeforeAcceptance ? 0 : 1;
        (await db.AccountSessions.CountAsync(row => row.UserId == user.Id)).ShouldBe(expected);
        probe.Accepted.Count.ShouldBe(expected);
        // The injected failure precedes the real protocol handler, so the device still awaits approval.
        using var pending = await client.PostAsync("/connect/token", CreateDeviceTokenRequest(RequiredString(code, "device_code")));
        pending.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        RequiredString(await ReadJsonObjectAsync(pending), "error").ShouldBe("authorization_pending");
    }
}
