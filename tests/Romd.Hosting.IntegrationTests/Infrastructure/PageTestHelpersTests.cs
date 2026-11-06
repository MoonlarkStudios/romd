using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

public sealed class PageTestHelpersTests
{
    [Fact]
    public async Task Collector_PreservesFiltersAndOpaqueCursorsAcrossEmptyIntermediatePages()
    {
        string[] responses =
        [
            """{"items":[{"id":"first"}],"nextCursor":"a+/= &","hasNextPage":true}""",
            """{"items":[],"nextCursor":"second","hasNextPage":true}""",
            """{"items":[{"id":"last"}],"nextCursor":null,"hasNextPage":false}"""
        ];
        var requests = new List<Uri>();
        using var client = Client((request, index) =>
        {
            requests.Add(request.RequestUri!);
            return Json(responses[index]);
        });

        var items = await TestHelpers.ReadAllPageItemsAsync<JsonElement>(client, "/feed?query=A%26B&limit=1");

        items.Select(item => item.GetProperty("id").GetString()).ShouldBe(["first", "last"]);
        requests.Count.ShouldBe(3);
        foreach (var request in requests)
        {
            var query = QueryHelpers.ParseQuery(request.Query);
            query["query"].ToString().ShouldBe("A&B");
            query["limit"].ToString().ShouldBe("1");
        }
        QueryHelpers.ParseQuery(requests[0].Query).ContainsKey("cursor").ShouldBeFalse();
        QueryHelpers.ParseQuery(requests[1].Query)["cursor"].ToString().ShouldBe("a+/= &");
        QueryHelpers.ParseQuery(requests[2].Query)["cursor"].ToString().ShouldBe("second");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"items":[],"hasNextPage":false}""")]
    [InlineData("""{"items":[],"nextCursor":null,"hasNextPage":true}""")]
    [InlineData("""{"items":[],"nextCursor":"","hasNextPage":true}""")]
    [InlineData("""{"items":[],"nextCursor":"unexpected","hasNextPage":false}""")]
    public async Task Collector_RejectsMalformedOrIncoherentPages(string body)
    {
        using var client = Client((_, _) => Json(body));
        await Should.ThrowAsync<InvalidOperationException>(
            () => TestHelpers.ReadAllPageItemsAsync<JsonElement>(client, "/feed"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Collector_RequiresExactGetSuccessStatus(HttpStatusCode status)
    {
        using var client = Client((_, _) =>
        {
            var response = Json("""{"items":[],"nextCursor":null,"hasNextPage":false}""");
            response.StatusCode = status;
            return response;
        });
        await Should.ThrowAsync<InvalidOperationException>(
            () => TestHelpers.ReadAllPageItemsAsync<JsonElement>(client, "/feed"));
    }

    [Fact]
    public async Task Collector_RejectsRepeatedCursor()
    {
        using var client = Client((_, _) => Json(
            """{"items":[],"nextCursor":"same","hasNextPage":true}"""));
        await Should.ThrowAsync<InvalidOperationException>(
            () => TestHelpers.ReadAllPageItemsAsync<JsonElement>(client, "/feed"));
    }

    [Fact]
    public async Task Collector_LaterFailureDoesNotReturnPartialItems()
    {
        using var client = Client((_, index) => index == 0
            ? Json("""{"items":[{"id":"partial"}],"nextCursor":"next","hasNextPage":true}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Should.ThrowAsync<HttpRequestException>(
            () => TestHelpers.ReadAllPageItemsAsync<JsonElement>(client, "/feed"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, int, HttpResponseMessage> respond) =>
        new(new Handler(respond)) { BaseAddress = new Uri("https://test.invalid") };

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, _index++));
    }
}
