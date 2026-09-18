using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Drives the OpenIddict authorization-code + PKCE flow (authorize -> interactive login ->
///     code -> token) against an in-process host. Pass a non-redirecting, cookie-handling client.
/// </summary>
internal static class OidcAuthorizationCodeFlow
{
    public static async Task<Dictionary<string, JsonElement>> RunAsync(
        HttpClient browser,
        string clientId,
        string redirectUri,
        string login,
        string password,
        string scope = "openid profile email roles offline_access")
    {
        string codeVerifier = CreateCodeVerifier();
        string authorizeUrl =
            "/connect/authorize?client_id=" + clientId +
            "&response_type=code&scope=" + Uri.EscapeDataString(scope) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&code_challenge=" + CreateCodeChallenge(codeVerifier) +
            "&code_challenge_method=S256&state=xyz";

        await browser.GetAsync(authorizeUrl);
        var loginResponse = await browser.PostAsync("/connect/login", Form(new Dictionary<string, string>
        {
            ["returnUrl"] = authorizeUrl,
            ["login"] = login,
            ["password"] = password
        }));

        var codeRedirect = await browser.GetAsync(loginResponse.Headers.Location);
        string code = ParseQueryValue(codeRedirect.Headers.Location!, "code");

        var tokenResponse = await browser.PostAsync("/connect/token", Form(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier
        }));

        string json = await tokenResponse.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    public static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string CreateCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string ParseQueryValue(Uri uri, string key)
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

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static FormUrlEncodedContent Form(IReadOnlyDictionary<string, string> values) => new(values);
}
