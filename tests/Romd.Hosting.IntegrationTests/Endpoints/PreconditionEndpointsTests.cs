using System.Net;
using System.Net.Http.Json;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     The shared precondition seam (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"),
///     exercised through a reference system: strong validators, 428 for an absent or wildcard If-Match, 412 for a
///     stale or weak tag, 400 for malformed syntax, ETag chaining across writes, create ETags, and weak
///     If-None-Match revalidation.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class PreconditionEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public async Task Patch_AbsentEmptyOrWildcardIfMatch_Is428AndWritesNothing(string? ifMatch)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (path, etag) = await CreateSystemAsync(client);

        using var response = await client.SendAsync(Patch(path, "Changed", ifMatch));

        using (await ProblemEnvelope.ReadAsync(response, 428, "ReferenceResource.PreconditionRequired"))
        { }
        response.Headers.ETag.ShouldBeNull();
        (await ReadAsync(client, path)).ETag.ShouldBe(etag);
    }

    [Theory]
    [InlineData("\"stale\"")]
    [InlineData("\"*\"")]
    [InlineData("W/{0}")]
    public async Task Patch_StaleOrWeakIfMatch_Is412AndWritesNothing(string ifMatchFormat)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (path, etag) = await CreateSystemAsync(client);

        using var response = await client.SendAsync(Patch(path, "Changed", string.Format(ifMatchFormat, etag)));

        using (await ProblemEnvelope.ReadAsync(response, 412, "ReferenceResource.PreconditionFailed"))
        { }
        (await ReadAsync(client, path)).ETag.ShouldBe(etag);
    }

    [Theory]
    [InlineData("not-a-quoted-tag")]
    [InlineData("\"unterminated")]
    [InlineData("W/\"unterminated")]
    [InlineData(",")]
    [InlineData(", ,")]
    [InlineData("{0}, not-a-quoted-tag")]
    [InlineData("{0}, *")]
    [InlineData("*, {0}")]
    [InlineData("*, *")]
    [InlineData("*,")]
    [InlineData(", *")]
    [InlineData(" , * , ")]
    public async Task Patch_MalformedIfMatch_IsAttributed400AndWritesNothing(string ifMatchFormat)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (path, etag) = await CreateSystemAsync(client);
        string ifMatch = string.Format(ifMatchFormat, etag);

        // TestServer copies HttpRequestHeaders by enumeration, which parses and normalizes valid list
        // members even after TryAddWithoutValidation (e.g. "*," becomes "*"). Supply the original value
        // at its ASP.NET request boundary so this exercises the endpoint with the exact malformed input.
        using var rawClient = new HttpClient(fixture.Server.CreateHandler(context => context.Request.Headers.IfMatch = ifMatch))
        {
            BaseAddress = client.BaseAddress
        };
        foreach (var header in client.DefaultRequestHeaders)
            rawClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();

        using var response = await rawClient.SendAsync(Patch(path, "Changed", ifMatch));

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().ShouldHaveSingleItem().Name.ShouldBe("If-Match");
        body.RootElement.GetProperty("errors").GetProperty("If-Match").GetArrayLength().ShouldBeGreaterThan(0);
        response.Headers.ETag.ShouldBeNull();
        var unchanged = await ReadAsync(client, path);
        unchanged.ETag.ShouldBe(etag);
        unchanged.Resource.Name.ShouldBe("Precondition System");
    }

    [Theory]
    [InlineData("\"stale\", {0}")]
    [InlineData("W/{0}, {0}")]
    [InlineData(", {0}, ,")]
    public async Task Patch_TagListHoldingTheCurrentTag_SucceedsAndEachResponseETagChainsIntoTheNextWrite(string ifMatchFormat)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (path, etag) = await CreateSystemAsync(client);

        using var first = await client.SendAsync(Patch(path, "First", string.Format(ifMatchFormat, etag)));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        string chained = StrongETag(first);
        chained.ShouldNotBe(etag);

        using var replay = await client.SendAsync(Patch(path, "Lost", etag));
        replay.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        using var second = await client.SendAsync(Patch(path, "Second", chained));
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await second.Content.ReadFromJsonAsync<SystemResource>())!.Name.ShouldBe("Second");
        (await ReadAsync(client, path)).ETag.ShouldBe(StrongETag(second));
    }

    [Theory]
    [InlineData("{0}", HttpStatusCode.NotModified)]
    [InlineData("*", HttpStatusCode.NotModified)]
    [InlineData("\"other\", {0}", HttpStatusCode.NotModified)]
    [InlineData("W/{0}", HttpStatusCode.NotModified)]
    [InlineData("\"other\"", HttpStatusCode.OK)]
    public async Task Get_IfNoneMatch_RevalidatesWithWeakComparison(string ifNoneMatchFormat, HttpStatusCode expected)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var (path, etag) = await CreateSystemAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("If-None-Match", string.Format(ifNoneMatchFormat, etag)).ShouldBeTrue();

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(expected);
        StrongETag(response).ShouldBe(etag);
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoCache.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/systems")]
    [InlineData("/api/companies")]
    public async Task Post_Create_AnswersWithTheCreatedResourcesETag(string collection)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var key = "local-pre-" + Guid.NewGuid().ToString("N");
        using var created = collection == "/api/systems"
            ? await client.PostAsJsonAsync(collection, new CreateSystem(key, "Created System", CompactLabel: "NEW"))
            : await client.PostAsJsonAsync(collection, new CreateCompany(key, "Created Company"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var read = await client.GetAsync(collection + "/" + key);
        StrongETag(created).ShouldBe(StrongETag(read));
    }

    private static async Task<(string Path, string ETag)> CreateSystemAsync(HttpClient client)
    {
        var key = "local-pre-" + Guid.NewGuid().ToString("N");
        using var created = await client.PostAsJsonAsync("/api/systems", new CreateSystem(key, "Precondition System", CompactLabel: "PRE"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return ("/api/systems/" + key, StrongETag(created));
    }

    private static async Task<(string ETag, SystemResource Resource)> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (StrongETag(response), (await response.Content.ReadFromJsonAsync<SystemResource>())!);
    }

    private static HttpRequestMessage Patch(string path, string name, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new SystemPatch { Name = name }) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    /// <summary>The response validator, asserted to be one strong, quoted tag.</summary>
    private static string StrongETag(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeFalse();
        etag.Tag.ShouldMatch("^\"[^\"]+\"$");
        return etag.Tag;
    }
}
