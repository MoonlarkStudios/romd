using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Mount;
using Romd.Mount.Credentials;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [RcloneTokenCommandFact]
    public Task Vfs_RcloneTokenCommand_RefreshesAfterUnauthorizedAndFailsAfterRefreshRevocation() => RunTokenCommandAsync(false);

    [RcloneKeychainFact]
    public Task Vfs_RcloneTokenCommand_KeychainBrokerPersistsRotationAndFailsAfterRevocation() => RunTokenCommandAsync(true);

    private static async Task RunTokenCommandAsync(bool keychain)
    {
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
            services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true }), networkListener: true);
        int libraryId = await fixture.CreateLibraryAsync("Token probe");
        await fixture.CreateConsumerUserAsync("token-probe", "token-probe@localhost", "Password123", user => user.LibraryId = libraryId);
        await using var source = await VfsProbeContent.CreateAsync();
        string path = await fixture.SeedVfsProbeAsync(libraryId, source);
        using var client = fixture.Factory.CreateClient();
        using var identity = new MountIdentityClient(client.BaseAddress!, allowLoopbackHttp: true);
        var boundIdentity = await identity.BindAsync();
        var login = await boundIdentity.BeginLoginAsync();
        using var approval = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify", new Dictionary<string, string>
        {
            ["user_code"] = login.UserCode,
            ["login"] = "token-probe",
            ["password"] = "Password123"
        }));
        approval.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await boundIdentity.PollLoginAsync(login.DeviceCode);
        // Inject an invalid bearer to exercise rclone's 401 recovery. Revoking an
        // issued token ends the ROMD session, so it cannot model ordinary expiry.
        string staleAccess = "invalid-probe-access-token";
        string refresh = issued.RefreshToken;
        string originalRefresh = refresh;
        await using var credentials = await VfsCredentialProbe.CreateAsync(boundIdentity, refresh, keychain);
        var broker = new MountTokenSession(boundIdentity, credentials.Scope, credentials.Store);
        string? lastAccess = null;

        async Task Revoke(string value, string kind)
        {
            using var response = await client.PostAsync("/connect/revocation", CreateFormContent(new Dictionary<string, string>
            {
                ["client_id"] = RomdOpenIddictClients.Mount,
                ["token"] = value,
                ["token_type_hint"] = kind
            }));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/vfs/library/"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staleAccess);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        int calls = 0;
        int refreshes = 0;
        using var gate = new SemaphoreSlim(1);
        await using var bridge = await VfsTokenCommandProbe.CreateAsync(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (++calls == 1)
                    return staleAccess;
                string access = await broker.GetAccessTokenAsync(lastAccess);
                lastAccess = access;
                refresh = (await credentials.Store.ReadAsync(credentials.Scope))!;
                refreshes++;
                return access;
            }
            finally { gate.Release(); }
        });
        string url = new Uri(client.BaseAddress!, "/vfs/library/").AbsoluteUri;
        async Task<(int Code, byte[] Data, string Error)> ReadWithCommand()
        {
            var start = RcloneStart(url, "", "cat", "romd:" + path, "--offset", "65530", "--count", "4096");
            bridge.Configure(start);
            // Authentication recovery needs a retry after the first challenged request.
            start.ArgumentList.Add("--low-level-retries");
            start.ArgumentList.Add("3");
            using var process = Process.Start(start)!;
            using var bytes = new MemoryStream();
            Task output = process.StandardOutput.BaseStream.CopyToAsync(bytes);
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            await output;
            return (process.ExitCode, bytes.ToArray(), await error);
        }
        var read = await ReadWithCommand();
        read.Code.ShouldBe(0, read.Error);
        read.Data.ShouldBe(source.ReadRange(65530, 4096));
        bridge.Calls.ShouldBeGreaterThanOrEqualTo(2);
        refreshes.ShouldBeGreaterThanOrEqualTo(1);
        (refresh != originalRefresh).ShouldBeTrue();

        // A fresh broker has no in-memory rotation and must load the persisted refresh token.
        broker = new MountTokenSession(boundIdentity, credentials.Scope, credentials.Store);
        (await ReadWithCommand()).Code.ShouldBe(0);
        refreshes.ShouldBeGreaterThanOrEqualTo(2);
        await Revoke(refresh, "refresh_token");
        int priorRefreshes = refreshes;
        var denied = await ReadWithCommand();
        denied.Code.ShouldNotBe(0);
        denied.Data.ShouldBeEmpty();
        refreshes.ShouldBe(priorRefreshes);
        denied.Error.Contains(refresh, StringComparison.Ordinal).ShouldBeFalse();
        denied.Error.Contains(staleAccess, StringComparison.Ordinal).ShouldBeFalse();
    }
}
