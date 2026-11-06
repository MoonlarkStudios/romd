using System.Net;
using System.Text.RegularExpressions;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class BrowserAuthenticationForm
{
    public static async Task<FormUrlEncodedContent> CreateAsync(HttpClient browser, string path, IReadOnlyDictionary<string, string> values)
    {
        // Device verification GET also authenticates the code, so include it exactly as a browser does.
        string url = values.TryGetValue("user_code", out var code)
            ? path + "?user_code=" + Uri.EscapeDataString(code) : path;
        using var response = await browser.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"(__RequestVerificationToken)\" value=\"([^\"]+)\"");
        match.Success.ShouldBeTrue("The interactive form must carry an antiforgery token.");
        var form = new Dictionary<string, string>(values)
        {
            [match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value)
        };
        return new FormUrlEncodedContent(form);
    }
}
