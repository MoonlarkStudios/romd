using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Closed query names (docs/decisions/api-contract-policy.md, "Strict Validation"): an /api operation accepts exactly
///     the query parameters it declares, by exact name, and only array parameters repeat. The declaration is derived from
///     the handler as minimal APIs bind it, held equal to ApiExplorer for every binding shape and to both live OpenAPI
///     documents, and enforced before the body is read or any parameter binds.
/// </summary>
public sealed class QueryNameValidationTests : IAsyncLifetime
{
    private const int SnesId = 17;

    private readonly BodyReads _reads = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _uploads;

    public async Task InitializeAsync()
    {
        _app = Build(MapShapes, _reads);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public void Declarations_AgreeWithApiExplorerForEveryBindingShape()
    {
        var descriptions = _app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .ToDictionary(item => item.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Single().EndpointName);
        var endpoints = ApiEndpoints(_app);

        endpoints.Length.ShouldBe(descriptions.Count);
        foreach (var endpoint in endpoints)
        {
            string name = endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName;
            var expected = descriptions[name].ParameterDescriptions
                .Where(parameter => parameter.Source == BindingSource.Query)
                .Select(parameter => KeyValuePair.Create(parameter.Name, IsCollection(parameter.Type)));
            Describe(endpoint.Metadata.GetRequiredMetadata<QueryParameterDeclaration>().Parameters).ShouldBe(Describe(expected), name);
        }

        // The shapes that distinguish the rule, so agreement is not vacuous.
        Declared("Arrays").ShouldBe(["ids[]", "kinds[]", "tag[]", "values[]"]);
        Declared("DeleteArrays").ShouldBe(["tag[]"]);
        Declared("Uris").ShouldBe(["alternatives[]", "target"]);
        Declared("Posts").ShouldBe(["mode"]);
        Declared("Tagged").ShouldBe(["tag[]"]);
        Declared("Routed").ShouldBe(["other"]);
        Declared("Systems").ShouldBe(["relatedSystemKey"]);
        Declared("Ambient").ShouldBeEmpty();
        Declared("Upload").ShouldBe(["maxParallel"]);
    }

    [Theory]
    [InlineData("grouped")]
    [InlineData("custom")]
    [InlineData("inherited")]
    public async Task GroupedOrCustomBoundParameters_AreRefusedWhenTheEndpointIsBuilt(string shape)
    {
        await using var app = Build(api =>
        {
            if (shape == "grouped")
                api.MapGet("/grouped", ([AsParameters] Grouped query) => query.Name);
            else if (shape == "inherited")
                api.MapGet("/custom", (InheritedCustomBound bound) => "ok");
            else
                api.MapGet("/custom", (CustomBound bound) => "ok");
        }, new BodyReads());

        Should.Throw<Exception>(() => ApiEndpoints(app)).GetBaseException().ShouldBeOfType<NotSupportedException>();
    }

    [Theory]
    [InlineData("GET", "/api/plain?x=1", "x")]
    [InlineData("GET", "/api/plain?x", "x")]
    [InlineData("GET", "/api/scalars?limit=1&limit=2", "limit")]
    [InlineData("GET", "/api/scalars?query=a&query=b", "query")]
    [InlineData("GET", "/api/scalars?Limit=5", "Limit")]
    [InlineData("GET", "/api/scalars?limit=5&LIMIT=6", "LIMIT")]
    [InlineData("GET", "/api/arrays?Tag=a&tag=b", "Tag")]
    [InlineData("GET", "/api/arrays?tag=a&Tag=b", "Tag")]
    [InlineData("GET", "/api/renamed?text=a", "text")]
    [InlineData("GET", "/api/renamed?%51=a", "Q")]
    [InlineData("GET", "/api/routes/one?name=two", "name")]
    [InlineData("GET", "/api/systems/snes?systemKey=snes", "systemKey")]
    [InlineData("GET", "/api/ambient?X-Thing=a", "X-Thing")]
    [InlineData("POST", "/api/posts?items=a", "items")]
    public async Task UndeclaredOrRepeatedName_Is400KeyedByTheSuppliedName(string method, string path, string field)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("[\"a\"]", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        Fields(body).ShouldBe([field]);
    }

    [Theory]
    [InlineData("GET", "/api/plain")]
    [InlineData("GET", "/api/plain?")]
    [InlineData("GET", "/api/scalars?query=a&limit=3&kind=Alpha&flag=true")]
    [InlineData("GET", "/api/arrays?tag=a&tag=b&kinds=Alpha&kinds=Beta&ids=1&ids=2&values=x&values=y")]
    [InlineData("GET", "/api/uris?target=https%3A%2F%2Fexample.test&alternatives=relative&alternatives=https%3A%2F%2Fexample.test")]
    [InlineData("GET", "/api/renamed?q=a")]
    [InlineData("GET", "/api/renamed?%71=a")]
    [InlineData("GET", "/api/routes/one?other=two")]
    [InlineData("GET", "/api/systems/snes?relatedSystemKey=snes")]
    [InlineData("DELETE", "/api/arrays?tag=a&tag=b")]
    [InlineData("POST", "/api/posts?mode=a")]
    public async Task DeclaredNames_ReachTheHandler(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = new StringContent("[\"a\"]", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UndeclaredName_IsReportedAloneAheadOfBindingAndDeclaredConstraints()
    {
        using (var scalars = await _client.GetAsync("/api/scalars?bogus=1&id=not-a-guid&limit=99"))
        using (var body = await ProblemEnvelope.ReadAsync(scalars, 400, "Request.InvalidParameter"))
            Fields(body).ShouldBe(["bogus"]);

        using var malformed = await _client.PostAsync("/api/tagged?bogus=1", new StringContent("{", Encoding.UTF8, "application/json"));
        using var malformedBody = await ProblemEnvelope.ReadAsync(malformed, 400, "Request.InvalidParameter");
        Fields(malformedBody).ShouldBe(["bogus"]);
    }

    [Fact]
    public async Task UploadWithAnUndeclaredName_IsRefusedBeforeItsBodyIsReadOrItsHandlerRuns()
    {
        using (var refused = await _client.PostAsync("/api/upload?maxParalel=2", Upload()))
        using (var body = await ProblemEnvelope.ReadAsync(refused, 400, "Request.InvalidParameter"))
            Fields(body).ShouldBe(["maxParalel"]);
        _reads.Count.ShouldBe(0);
        _uploads.ShouldBe(0);

        // The same upload with its declared name is read and handled, so the tracker observes real reads.
        using var accepted = await _client.PostAsync("/api/upload?maxParallel=2", Upload());
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        _reads.Count.ShouldBeGreaterThan(0);
        _uploads.ShouldBe(1);
    }

    [Fact]
    public async Task RoutingRefusals_KeepTheirOwnEnvelopes()
    {
        using (var unmatched = await _client.GetAsync("/api/no-such-route?x=1"))
        using (var _ = await ProblemEnvelope.ReadAsync(unmatched, 404, "Request.RouteNotFound"))
        { }
        using (var wrongMethod = await _client.PutAsync("/api/plain?x=1", null))
        using (var _ = await ProblemEnvelope.ReadAsync(wrongMethod, 405, "Request.MethodNotAllowed"))
        { }
        using var unsupported = await _client.PostAsync("/api/tagged?x=1", new StringContent("{}", Encoding.UTF8, "text/plain"));
        using var __ = await ProblemEnvelope.ReadAsync(unsupported, 415, "Request.UnsupportedMediaType");
    }

    [Fact]
    public async Task ApiEndpointWithoutADeclaration_IsASanitizedServerError()
    {
        await using var app = Build(_ => { }, new BodyReads());
        app.MapGet("/api/outside", () => "reached");
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/outside");

        using var body = await ProblemEnvelope.ReadAsync(response, 500, "General.Unhandled");
        body.RootElement.GetRawText().ShouldNotContain(nameof(RomdHttpContractExtensions.WithRomdParameterValidation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveHost_EveryApiOperationDeclaresExactlyItsDocumentedQueryAndThe400(bool consumer)
    {
        await using var host = LiveHost.Start(consumer);
        using var document = await host.ReadOpenApiAsync();
        var operations = DocumentedOperations(document);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(IsApiRoute)
            .ToArray();

        var problems = new List<string>();
        foreach (var endpoint in endpoints)
        {
            string id = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "(unnamed)";
            if (endpoint.Metadata.GetMetadata<QueryParameterDeclaration>() is not { } declaration)
                problems.Add($"{id}: no query declaration");
            else if (!operations.TryGetValue(id, out var operation))
                problems.Add($"{id}: no documented operation");
            else
            {
                if (!Describe(declaration.Parameters).SequenceEqual(Describe(operation.Query)))
                    problems.Add($"{id}: declares [{string.Join(", ", Describe(declaration.Parameters))}], documents [{string.Join(", ", Describe(operation.Query))}]");
                if (!operation.Documents400)
                    problems.Add($"{id}: documents no 400");
            }
        }

        endpoints.ShouldNotBeEmpty();
        problems.ShouldBeEmpty();
        operations.Keys.Order(StringComparer.Ordinal).ShouldBe(endpoints
            .Select(endpoint => endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName)
            .Order(StringComparer.Ordinal));
    }

    private void MapShapes(RouteGroupBuilder api)
    {
        api.MapGet("/plain", () => "ok").WithName("Plain");
        api.MapGet("/scalars", (string? query, Guid? id, bool? flag, Kind? kind, DateTimeOffset? from, [Range(1, 10)] int limit = 5) => "ok")
            .WithName("Scalars");
        api.MapGet("/renamed", ([FromQuery(Name = "q")] string? text) => text ?? "").WithName("Renamed");
        api.MapGet("/routes/{name}", (string name, string? other) => name).WithName("Routed");
        api.MapGet("/systems/{systemKey}", (SystemKeyParameter systemKey,
            [FromQuery(Name = "relatedSystemKey")] SystemKeyParameter? related) => "ok").WithName("Systems");
        api.MapGet("/arrays", (string[]? tag, Kind[]? kinds, int[]? ids, StringValues? values) => "ok").WithName("Arrays");
        api.MapDelete("/arrays", (string[]? tag) => "ok").WithName("DeleteArrays");
        api.MapGet("/uris", (Uri? target, Uri[]? alternatives) => "ok").WithName("Uris");
        api.MapPost("/posts", (string[] items, string? mode) => "ok").WithName("Posts");
        api.MapPost("/tagged", ([FromQuery] string[]? tag, [FromBody] Payload payload) => "ok").WithName("Tagged");
        api.MapGet("/ambient", ([FromHeader(Name = "X-Thing")] string? thing, [FromServices] IReferenceCatalogService catalog,
            HttpContext context, HttpRequest request, ClaimsPrincipal user, CancellationToken ct) => "ok").WithName("Ambient");
        api.MapPost("/upload", (IFormFile file, int? maxParallel) =>
        {
            _uploads++;
            return "ok";
        }).WithName("Upload").DisableAntiforgery();
    }

    private string[] Declared(string name) =>
        Describe(ApiEndpoints(_app)
            .Single(endpoint => endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName == name)
            .Metadata.GetRequiredMetadata<QueryParameterDeclaration>().Parameters);

    private static WebApplication Build(Action<RouteGroupBuilder> map, BodyReads reads)
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.ResolveSystemIdAsync("snes", Arg.Any<CancellationToken>()).Returns(SnesId);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSingleton(catalog);
        var app = builder.Build();
        app.UseRomdExceptionHandling();
        // Ahead of the pipeline, so any read of the request body is counted.
        app.Use((context, next) =>
        {
            context.Request.Body = new ReadCountingStream(context.Request.Body, reads);
            return next(context);
        });
        app.UseRouting();
        app.UseRomdApiRoutingProblems();
        app.UseRomdQueryNameValidation();
        map(app.MapGroup("/api").WithRomdParameterValidation());
        return app;
    }

    private static MultipartFormDataContent Upload() =>
        new() { { new ByteArrayContent(new byte[4096]), "file", "game.bin" } };

    private static RouteEndpoint[] ApiEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(IsApiRoute)
            .ToArray();

    private static bool IsApiRoute(RouteEndpoint route) =>
        (route.RoutePattern.RawText ?? string.Empty).TrimStart('/').StartsWith("api/", StringComparison.Ordinal);

    private static bool IsCollection(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsArray || underlying == typeof(StringValues);
    }

    private static string[] Describe(IEnumerable<KeyValuePair<string, bool>> parameters) =>
        parameters.Select(parameter => parameter.Value ? parameter.Key + "[]" : parameter.Key).Order(StringComparer.Ordinal).ToArray();

    private static string[] Fields(JsonDocument body) =>
        body.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ToArray();

    /// <summary>Each documented /api operation by operationId: its query parameters (array means repeatable) and whether it documents 400.</summary>
    private static Dictionary<string, (IEnumerable<KeyValuePair<string, bool>> Query, bool Documents400)> DocumentedOperations(JsonDocument document)
    {
        var operations = new Dictionary<string, (IEnumerable<KeyValuePair<string, bool>>, bool)>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject().Where(path => path.Name.StartsWith("/api/", StringComparison.Ordinal)))
            foreach (var operation in path.Value.EnumerateObject().Where(item => item.Name is "get" or "post" or "put" or "patch" or "delete"))
            {
                var parameters = (path.Value.TryGetProperty("parameters", out var shared) ? shared.EnumerateArray().ToArray() : [])
                    .Concat(operation.Value.TryGetProperty("parameters", out var own) ? own.EnumerateArray().ToArray() : [])
                    .Where(parameter => parameter.GetProperty("in").GetString() == "query")
                    .Select(parameter => KeyValuePair.Create(parameter.GetProperty("name").GetString()!,
                        parameter.GetProperty("schema").TryGetProperty("type", out var type) && TypeNames(type).Contains("array")))
                    .ToArray();
                operations.Add(operation.Value.GetProperty("operationId").GetString()!,
                    (parameters, operation.Value.GetProperty("responses").TryGetProperty("400", out _)));
            }
        return operations;
    }

    private static string[] TypeNames(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()!).ToArray() : [type.GetString()!];

    public enum Kind
    {
        Alpha,
        Beta
    }

    public sealed record Payload(string Name);

    public readonly record struct Grouped(string? Name);

    public sealed class CustomBound
    {
        public static ValueTask<CustomBound?> BindAsync(HttpContext context) => ValueTask.FromResult<CustomBound?>(new CustomBound());
    }

    public class CustomBoundBase
    {
        public static ValueTask<InheritedCustomBound?> BindAsync(HttpContext context) =>
            ValueTask.FromResult<InheritedCustomBound?>(new InheritedCustomBound());
    }

    public sealed class InheritedCustomBound : CustomBoundBase;

    private sealed class BodyReads
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record() => Interlocked.Increment(ref _count);
    }

    /// <summary>A request body that counts every read that returns bytes.</summary>
    private sealed class ReadCountingStream(Stream inner, BodyReads reads) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Counted(int read)
        {
            if (read > 0)
                reads.Record();
            return read;
        }
    }

    /// <summary>A started host (Development, so it serves its OpenAPI document).</summary>
    private sealed class LiveHost : IAsyncDisposable
    {
        private const string Secret = "query-name-validation-secret-at-least-32-characters";

        private readonly IAsyncDisposable _factory;
        private readonly HttpClient _client;
        private readonly string _dataDirectory;
        private readonly bool _consumer;

        private LiveHost(IAsyncDisposable factory, HttpClient client, IServiceProvider services, string dataDirectory, bool consumer)
        {
            _factory = factory;
            _client = client;
            Services = services;
            _dataDirectory = dataDirectory;
            _consumer = consumer;
        }

        public IServiceProvider Services { get; }

        public static LiveHost Start(bool consumer)
        {
            string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-query-names-{Guid.NewGuid():N}");
            if (consumer)
            {
                var factory = RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(dataDirectory, "Development", Secret);
                return new LiveHost(factory, factory.CreateClient(), factory.Services, dataDirectory, consumer);
            }

            var admin = RomdHostTestFactory.Create<Romd.Admin.Host.Program>(dataDirectory, "Development", Secret);
            return new LiveHost(admin, admin.CreateClient(), admin.Services, dataDirectory, consumer);
        }

        public async Task<JsonDocument> ReadOpenApiAsync() =>
            JsonDocument.Parse(await _client.GetStringAsync($"/openapi/{(_consumer ? "consumer-v1" : "admin-v1")}.json"));

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
