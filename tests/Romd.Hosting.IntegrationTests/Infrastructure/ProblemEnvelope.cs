using System.Text.Json;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Response assertions for the shared error envelope
///     (docs/decisions/api-contract-policy.md, "One Error Envelope"), used by the admin and
///     consumer envelope tests.
/// </summary>
internal static class ProblemEnvelope
{
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Reads an envelope and asserts its status, stable errorCode and request trace identifier.
    /// </summary>
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, int status, string errorCode)
    {
        ((int)response.StatusCode).ShouldBe(status);
        var body = await ReadAsync(response);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(status);
        root.GetProperty("errorCode").GetString().ShouldBe(errorCode);
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();

        return body;
    }

    /// <summary>
    ///     Asserts a sanitized 500: the generic envelope with no exception message, type or detail.
    /// </summary>
    public static async Task AssertSanitizedServerErrorAsync(
        HttpResponseMessage response,
        string secretExceptionMessage,
        Type exceptionType)
    {
        string rawBody = await response.Content.ReadAsStringAsync();
        rawBody.ShouldNotContain(secretExceptionMessage);
        rawBody.ShouldNotContain(exceptionType.Name);

        using var body = await ReadAsync(response, 500, "General.Unhandled");
        var root = body.RootElement;

        root.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        if (root.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null)
        {
            throw new ShouldAssertException(
                $"500 responses must not carry exception details, but got: {detail}");
        }
    }
}
