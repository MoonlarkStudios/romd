using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     The shared binding-failure envelope against the framework's real binding messages
///     (docs/decisions/api-contract-policy.md, "Strict Validation"): a route, query or header
///     parameter failure is <c>Request.InvalidParameter</c> keyed by the parameter's wire name, a body
///     failure is <c>Request.InvalidBody</c>, and 415 keeps its own code. A framework upgrade that
///     changes a binding message fails here.
/// </summary>
public sealed class BindingFailureEnvelopeTests : IAsyncLifetime
{
    private const string RejectedValue = "rejected-value-zq";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        _app = builder.Build();
        _app.UseRomdExceptionHandling();
        _app.UseRomdApiRoutingProblems();
        _app.MapGet("/api/items/{itemId}", (int itemId) => itemId);
        _app.MapGet("/api/items", (int limit) => limit);
        _app.MapGet("/api/pairs", (int first, int second) => first + second);
        _app.MapGet("/api/search", ([FromQuery] string term) => term);
        _app.MapGet("/api/requests", ([FromHeader(Name = "X-Request-Id")] Guid requestId) => requestId);
        _app.MapPost("/api/payloads", (Payload payload) => payload);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/items/" + RejectedValue, "itemId")]
    [InlineData("/api/items?limit=" + RejectedValue, "limit")]
    [InlineData("/api/search", "term")]
    public async Task RouteOrQueryParameterFailure_NamesTheParameter(string path, string parameter)
    {
        using var response = await _client.GetAsync(path);

        await AssertInvalidParameterAsync(response, parameter);
    }

    // Binding stops at the first parameter that fails to bind (docs/decisions/api-contract-policy.md,
    // "One Error Envelope").
    [Fact]
    public async Task SeveralUnbindableParameters_NameOnlyTheFirst()
    {
        using var response = await _client.GetAsync($"/api/pairs?first={RejectedValue}&second={RejectedValue}");

        await AssertInvalidParameterAsync(response, "first");
    }

    [Fact]
    public async Task HeaderParameterFailure_NamesTheHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/requests");
        request.Headers.Add("X-Request-Id", RejectedValue);

        using var response = await _client.SendAsync(request);

        await AssertInvalidParameterAsync(response, "X-Request-Id");
    }

    [Fact]
    public async Task MissingHeaderParameter_NamesTheHeader()
    {
        using var response = await _client.GetAsync("/api/requests");

        await AssertInvalidParameterAsync(response, "X-Request-Id");
    }

    [Fact]
    public async Task MalformedJsonBody_IsAnInvalidBodyWithoutParameterErrors()
    {
        using var content = new StringContent("{\"name\":", Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("/api/payloads", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody");
        body.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UnsupportedBodyMediaType_Returns415Envelope()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        using var response = await _client.PostAsync("/api/payloads", content);

        using var body = await ProblemEnvelope.ReadAsync(response, 415, "Request.UnsupportedMediaType");
    }

    private static async Task AssertInvalidParameterAsync(HttpResponseMessage response, string parameter)
    {
        string raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain(RejectedValue);
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).ShouldBe([parameter]);
        errors.GetProperty(parameter).EnumerateArray().Select(message => message.GetString())
            .ShouldAllBe(message => !string.IsNullOrWhiteSpace(message));
    }

    public sealed record Payload(string Name);
}
