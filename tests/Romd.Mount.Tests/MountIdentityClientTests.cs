using System.Net;
using System.Net.Sockets;
using System.Text;
using Romd.Mount;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountIdentityClientTests
{
    [Theory]
    [InlineData("http://server.example")]
    [InlineData("https://user:secret@server.example")]
    [InlineData("https://server.example/path")]
    [InlineData("https://server.example/?token=secret")]
    [InlineData("https://server.example/#secret")]
    [InlineData("http://localhost.evil.example")]
    public void Origin_UnsafeAddress_RejectsWithoutEchoingInput(string address)
    {
        var error = Should.Throw<ArgumentException>(() => new MountIdentityClient(new Uri(address), allowLoopbackHttp: true));
        error.Message.ShouldNotContain(address);
    }

    [Fact]
    public void Origin_LoopbackHttp_RequiresExplicitOptIn()
    {
        Should.Throw<ArgumentException>(() => new MountIdentityClient(new Uri("http://127.0.0.1:5002")));
        using var client = new MountIdentityClient(new Uri("http://127.0.0.1:5002"), allowLoopbackHttp: true);
    }

    [Fact]
    public async Task BeginLogin_ValidResponse_UsesMountClientAndReturnsUserInstructions()
    {
        string? form = null;
        using var client = Client(async (request, _) =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/connect/device");
            form = await request.Content!.ReadAsStringAsync();
            return Json("""{"device_code":"private-code","user_code":"ABCD","verification_uri":"https://server.example/connect/verify","expires_in":600}""");
        });
        var login = await client.BeginLoginAsync();
        form!.ShouldContain("client_id=romd-mount");
        login.PollIntervalSeconds.ShouldBe(5);
        login.UserCode.ShouldBe("ABCD");
        login.ToString().ShouldNotContain("private-code");
    }

    [Theory]
    [InlineData("https://other.example/verify")]
    [InlineData("http://server.example/verify")]
    [InlineData("https://server.example:444/verify")]
    [InlineData("https://user:pass@server.example/verify")]
    public async Task BeginLogin_ForeignVerificationUri_Rejects(string uri)
    {
        using var client = Client((_, _) => Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            device_code = "private",
            user_code = "ABCD",
            verification_uri = uri,
            expires_in = 600
        }))));
        (await Should.ThrowAsync<MountIdentityException>(() => client.BeginLoginAsync())).Failure.ShouldBe(IdentityFailure.InvalidResponse);
    }

    [Theory]
    [InlineData("authorization_pending", IdentityFailure.Pending)]
    [InlineData("slow_down", IdentityFailure.SlowDown)]
    [InlineData("access_denied", IdentityFailure.Denied)]
    [InlineData("expired_token", IdentityFailure.Expired)]
    [InlineData("invalid_grant", IdentityFailure.SignInRequired)]
    [InlineData("unknown", IdentityFailure.Rejected)]
    public async Task PollLogin_ProtocolError_ReturnsSafeClassification(string code, IdentityFailure expected)
    {
        using var client = Client((_, _) => Task.FromResult(Json($$"""{"error":"{{code}}","error_description":"secret-token"}""", HttpStatusCode.BadRequest)));
        var error = await Should.ThrowAsync<MountIdentityException>(() => client.PollLoginAsync("device-secret"));
        error.Failure.ShouldBe(expected);
        error.ToString().ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Bearer\",\"expires_in\":\"900\"}")]
    public async Task Refresh_MalformedResponse_FailsClosed(string json)
    {
        using var client = Client((_, _) => Task.FromResult(Json(json)));
        (await Should.ThrowAsync<MountIdentityException>(() => client.RefreshAsync("refresh-secret"))).Failure.ShouldBe(IdentityFailure.InvalidResponse);
    }

    [Fact]
    public async Task Refresh_ValidRotation_ReturnsBothTokensWithoutPrintingSecrets()
    {
        using var client = Client(async (request, _) =>
        {
            string form = await request.Content!.ReadAsStringAsync();
            form.ShouldContain("refresh_token=old%2Btoken");
            request.RequestUri!.Query.ShouldBeEmpty();
            return Json("""{"access_token":"access-secret","refresh_token":"new-secret","token_type":"Bearer","expires_in":900}""");
        });
        var tokens = await client.RefreshAsync("old+token");
        tokens.RefreshToken.ShouldBe("new-secret");
        tokens.ToString().ShouldNotContain("secret");
    }

    [Fact]
    public async Task Refresh_OversizedResponse_FailsClosed()
    {
        using var client = Client((_, _) => Task.FromResult(Json(new string('x', 65537))));
        (await Should.ThrowAsync<MountIdentityException>(() => client.RefreshAsync("secret"))).Failure.ShouldBe(IdentityFailure.InvalidResponse);
    }

    [Fact]
    public async Task Refresh_CallerCancellation_RemainsCancellation()
    {
        using var client = Client(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        using var cancel = new CancellationTokenSource();
        var task = client.RefreshAsync("secret", cancel.Token);
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Refresh_RealHttpRedirect_IsRejectedWithoutFollowing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        using var client = new MountIdentityClient(address, allowLoopbackHttp: true);
        Task<MountTokens> request = client.RefreshAsync("secret", timeout.Token);
        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = connection.GetStream();
        // Read the headers before sending a redirect. No credentials are printed.
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 })
        { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 307 Temporary Redirect\r\nLocation: /elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        connection.Close();
        (await Should.ThrowAsync<MountIdentityException>(() => request)).Failure.ShouldBe(IdentityFailure.Rejected);
        listener.Pending().ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Refresh_TemporaryServerFailure_DoesNotDemandNewSignIn(HttpStatusCode status)
    {
        using var client = Client((_, _) => Task.FromResult(Json("secret server diagnostics", status)));
        var error = await Should.ThrowAsync<MountIdentityException>(() => client.RefreshAsync("secret"));
        error.Failure.ShouldBe(IdentityFailure.Network);
        error.ToString().ShouldNotContain("secret");
    }

    private static MountIdentityClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Uri("https://server.example"), new Handler(send));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    public async Task Revoke_Success_AcceptsEmptyOrJsonResponse(string body)
    {
        using var client = Client(async (request, _) =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/connect/revocation");
            request.RequestUri.Query.ShouldBeEmpty();
            string form = await request.Content!.ReadAsStringAsync();
            form.ShouldContain("token=private%2Btoken");
            form.ShouldContain("token_type_hint=refresh_token");
            form.ShouldContain("client_id=romd-mount");
            return Json(body);
        });
        await client.RevokeAsync("private+token");
    }

    [Fact]
    public async Task Revoke_Unavailable_DoesNotReportSuccess()
    {
        using var client = Client((_, _) => Task.FromResult(Json("secret", HttpStatusCode.ServiceUnavailable)));
        (await Should.ThrowAsync<MountIdentityException>(() => client.RevokeAsync("private"))).Failure.ShouldBe(IdentityFailure.Network);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"instanceId\":\"invalid\"}")]
    [InlineData("{\"instanceId\":\"11111111-2222-3333-4444-ABCDEF123456\"}")]
    [InlineData("{\"instanceId\":\"11111111-2222-3333-4444-abcdef123456\",\"other\":true}")]
    [InlineData("{\"instanceId\":\"11111111-2222-3333-4444-abcdef123456\",\"instanceId\":\"11111111-2222-3333-4444-abcdef123456\"}")]
    public async Task Discovery_InvalidContract_Rejects(string json)
    {
        using var client = Client((request, _) =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.ShouldBe("/api/server/identity");
            request.Headers.Authorization.ShouldBeNull();
            request.Headers.Contains("Cookie").ShouldBeFalse();
            request.Content.ShouldBeNull();
            return Task.FromResult(Json(json));
        });
        (await Should.ThrowAsync<MountIdentityException>(() => client.DiscoverInstanceAsync())).Failure.ShouldBe(IdentityFailure.InvalidResponse);
    }

    [Fact]
    public async Task Bind_ReplacedInstance_FailsBeforeAnyTokenExchange()
    {
        using var client = Client((request, _) =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            return Task.FromResult(Json("""{"instanceId":"11111111-2222-3333-4444-abcdef123456"}"""));
        });
        (await Should.ThrowAsync<MountIdentityException>(() => client.BindAsync(Guid.NewGuid()))).Failure.ShouldBe(IdentityFailure.ServerChanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundRefresh_DiscoveryFailsOrChanges_NeverSendsRefreshToken(bool unavailable)
    {
        int calls = 0;
        using var client = Client((request, _) =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            request.Headers.Authorization.ShouldBeNull();
            request.Content.ShouldBeNull();
            if (++calls == 1)
                return Task.FromResult(Json("""{"instanceId":"11111111-2222-3333-4444-abcdef123456"}"""));
            return Task.FromResult(unavailable ? Json("", HttpStatusCode.ServiceUnavailable)
                : Json("""{"instanceId":"22222222-2222-3333-4444-abcdef123456"}"""));
        });
        var bound = await client.BindAsync();
        var error = await Should.ThrowAsync<MountIdentityException>(() => bound.RefreshAsync("private-refresh"));
        error.Failure.ShouldBe(unavailable ? IdentityFailure.Network : IdentityFailure.ServerChanged);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task BoundRefresh_SameInstance_ChecksBeforeSendingCredential()
    {
        var methods = new List<HttpMethod>();
        using var client = Client((request, _) =>
        {
            methods.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Json("""{"instanceId":"11111111-2222-3333-4444-abcdef123456"}""")
                : Json("""{"access_token":"access","refresh_token":"rotated","token_type":"Bearer","expires_in":900}"""));
        });
        var bound = await client.BindAsync();
        (await bound.RefreshAsync("private-refresh")).RefreshToken.ShouldBe("rotated");
        methods.ShouldBe(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post });
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
