using System.Net;
using System.Text.Json;

namespace Romd.Mount;

public enum IdentityFailure { Network, InvalidResponse, Pending, SlowDown, Denied, Expired, SignInRequired, Rejected, ServerChanged }

/// <summary>Contains a fixed diagnostic code, never server text or credential values.</summary>
public sealed class MountIdentityException(IdentityFailure failure) : Exception($"Mount authentication failed: {failure}.")
{
    public IdentityFailure Failure { get; } = failure;
}

public sealed class DeviceLogin(string deviceCode, string userCode, Uri verificationUri, int expiresIn, int interval)
{
    public string DeviceCode { get; } = deviceCode;
    public string UserCode { get; } = userCode;
    public Uri VerificationUri { get; } = verificationUri;
    public int ExpiresInSeconds { get; } = expiresIn;
    public int PollIntervalSeconds { get; } = interval;
    public override string ToString() => "Device login (credentials redacted)";
}

public sealed class MountTokens(string accessToken, string refreshToken, int expiresIn)
{
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public int ExpiresInSeconds { get; } = expiresIn;
    public override string ToString() => "Mount tokens (redacted)";
}

/// <summary>
/// Mount's device-flow transport. Redirects, cookies, and ambient credentials are disabled.
/// The caller owns polling deadlines, secure persistence, and serialized renewal.
/// </summary>
public sealed class MountIdentityClient : IDisposable
{
    public const string ClientId = "romd-mount";
    private const int MaximumResponseBytes = 65536;
    private readonly HttpClient _http;
    public Uri Origin { get; }

    public MountIdentityClient(Uri origin, bool allowLoopbackHttp = false)
        : this(origin, new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        }, allowLoopbackHttp) { }

    internal MountIdentityClient(Uri origin, HttpMessageHandler handler, bool allowLoopbackHttp = false)
    {
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            !(origin.Scheme == Uri.UriSchemeHttps ||
              allowLoopbackHttp && origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
        {
            handler.Dispose();
            throw new ArgumentException("Use an HTTPS server origin without a path, credentials, query, or fragment.", nameof(origin));
        }
        Origin = origin;
        _http = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<DeviceLogin> BeginLoginAsync(CancellationToken ct = default)
    {
        using var result = await PostAsync("connect/device", new()
        {
            ["scope"] = "offline_access email profile roles"
        }, ct);
        var root = result.RootElement;
        string verification = Text(root, "verification_uri");
        if (!Uri.TryCreate(verification, UriKind.Absolute, out var uri) ||
            uri.Scheme != Origin.Scheme || uri.IdnHost != Origin.IdnHost || uri.Port != Origin.Port ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new MountIdentityException(IdentityFailure.InvalidResponse);
        int interval = root.TryGetProperty("interval", out var value) ? Number(value, 1, 300) : 5;
        return new(Text(root, "device_code"), Text(root, "user_code"), uri,
            RequiredNumber(root, "expires_in", 1, 86400), interval);
    }

    /// <summary>Discovers the instance without sending cookies, tokens, or device codes.</summary>
    public async Task<Guid> DiscoverInstanceAsync(CancellationToken ct = default)
    {
        using var result = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/server/identity"), ct, protocolErrors: false);
        var root = result.RootElement;
        string value = Text(root, "instanceId");
        if (root.EnumerateObject().Count() != 1 || !Guid.TryParseExact(value, "D", out var instance) ||
            value != instance.ToString("D"))
            throw new MountIdentityException(IdentityFailure.InvalidResponse);
        return instance;
    }

    public async Task<BoundMountIdentity> BindAsync(Guid? expectedInstance = null, CancellationToken ct = default)
    {
        Guid instance = await DiscoverInstanceAsync(ct);
        if (expectedInstance.HasValue && expectedInstance != instance)
            throw new MountIdentityException(IdentityFailure.ServerChanged);
        return new BoundMountIdentity(this, instance);
    }

    public Task<MountTokens> PollLoginAsync(string deviceCode, CancellationToken ct = default) => ExchangeAsync(new()
    {
        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode
    }, ct);

    public Task<MountTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) => ExchangeAsync(new()
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
    }, ct);

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        using var result = await PostAsync("connect/revocation", new()
        {
            ["token"] = refreshToken, ["token_type_hint"] = "refresh_token"
        }, ct, allowEmptySuccess: true);
    }

    private async Task<MountTokens> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var result = await PostAsync("connect/token", fields, ct);
        var root = result.RootElement;
        if (!string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new MountIdentityException(IdentityFailure.InvalidResponse);
        string access = Text(root, "access_token"), refresh = Text(root, "refresh_token");
        if (access.Any(char.IsWhiteSpace) || refresh.Any(char.IsWhiteSpace))
            throw new MountIdentityException(IdentityFailure.InvalidResponse);
        return new(access, refresh,
            RequiredNumber(root, "expires_in", 1, 86400));
    }

    private Task<JsonDocument> PostAsync(string path, Dictionary<string, string> fields, CancellationToken ct, bool allowEmptySuccess = false)
    {
        fields["client_id"] = ClientId;
        return SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) }, ct, protocolErrors: true, allowEmptySuccess);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct, bool protocolErrors, bool allowEmptySuccess = false)
    {
        using var ownedRequest = request;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new MountIdentityException(IdentityFailure.Rejected);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new MountIdentityException(IdentityFailure.Network);
            if (!protocolErrors && response.StatusCode != HttpStatusCode.OK)
                throw new MountIdentityException(IdentityFailure.Rejected);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new MountIdentityException(IdentityFailure.InvalidResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new MountIdentityException(IdentityFailure.InvalidResponse);
                buffer.Write(chunk, 0, count);
            }
            if (allowEmptySuccess && response.StatusCode == HttpStatusCode.OK && buffer.Length == 0)
                return JsonDocument.Parse("{}");
            var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (response.IsSuccessStatusCode && document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            using (document)
            {
                string error = Text(document.RootElement, "error");
                throw new MountIdentityException(error switch
                {
                    "authorization_pending" => IdentityFailure.Pending,
                    "slow_down" => IdentityFailure.SlowDown,
                    "access_denied" => IdentityFailure.Denied,
                    "expired_token" => IdentityFailure.Expired,
                    "invalid_grant" => IdentityFailure.SignInRequired,
                    _ => IdentityFailure.Rejected
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MountIdentityException(IdentityFailure.Network); }
        catch (HttpRequestException) { throw new MountIdentityException(IdentityFailure.Network); }
        catch (IOException) { throw new MountIdentityException(IdentityFailure.Network); }
        catch (JsonException) { throw new MountIdentityException(IdentityFailure.InvalidResponse); }
        catch (InvalidOperationException) { throw new MountIdentityException(IdentityFailure.InvalidResponse); }
    }

    private static string Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 and <= 32768 } value || value.Any(char.IsControl))
            throw new MountIdentityException(IdentityFailure.InvalidResponse);
        return value;
    }

    private static int RequiredNumber(JsonElement root, string name, int min, int max) =>
        root.TryGetProperty(name, out var value) ? Number(value, min, max)
            : throw new MountIdentityException(IdentityFailure.InvalidResponse);

    private static int Number(JsonElement value, int min, int max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min && number <= max
            ? number : throw new MountIdentityException(IdentityFailure.InvalidResponse);

    public void Dispose() => _http.Dispose();
}
