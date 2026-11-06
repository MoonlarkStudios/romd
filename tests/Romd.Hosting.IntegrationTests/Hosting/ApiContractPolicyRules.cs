using System.Text.Json;
using System.Text.RegularExpressions;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>One <c>/api</c> operation of a host's OpenAPI document, with path-level parameters merged in.</summary>
internal sealed record ApiOperation(
    string Host,
    string Method,
    string Route,
    JsonElement Definition,
    IReadOnlyList<JsonElement> Parameters,
    JsonElement Schemas)
{
    public string Key => $"{Host} {Method} {Route}";

    public IEnumerable<JsonProperty> Responses => ApiContractPolicyRules.Members(Definition, "responses");

    public bool Documents(string status) => Responses.Any(response => response.Name == status);

    public static IReadOnlyList<ApiOperation> FromDocument(string host, JsonDocument document)
    {
        JsonElement schemas = ApiContractPolicyRules.ComponentSchemas(document);
        return
        [
            .. ApiContractPolicyRules.Members(document.RootElement, "paths")
                .Where(path => ApiContractPolicyRules.IsApiRoute(path.Name))
                .SelectMany(path => ApiContractPolicyRules.HttpOperations(path.Value)
                    .Select(member => new ApiOperation(
                        host,
                        member.Name.ToUpperInvariant(),
                        path.Name,
                        member.Value,
                        [
                            .. ApiContractPolicyRules.Elements(path.Value, "parameters"),
                            .. ApiContractPolicyRules.Elements(member.Value, "parameters")
                        ],
                        schemas)))
        ];
    }
}

/// <summary>A mechanical rule of the API contract policy, its remedy and the violation keys it detects.</summary>
internal sealed record PolicyRule(
    string Id,
    string Requirement,
    string Remedy,
    Func<IReadOnlyList<ApiOperation>, IEnumerable<string>> FindViolations);

/// <summary>
///     Detectors for the OpenAPI shape rules of <c>docs/decisions/api-contract-policy.md</c>. Keys are
///     <c>"host METHOD /route"</c>, refined by status (R8, R13 responses), <c>?query</c> parameter (R10, R11,
///     R13), <c>header:Name</c> (R11) or <c>requestBody.path</c> (R11 inline and R13 request bodies). Schema
///     rules key by <c>"host Schema.property"</c> (R11) or <c>"host Schema"</c> (R12). The host prefix is always
///     present so a key does not change when the other document gains or loses the same path.
/// </summary>
internal static class ApiContractPolicyRules
{
    public const string PolicyDocument = "docs/decisions/api-contract-policy.md";
    private const string ComponentSchemaPrefix = "#/components/schemas/";
    private const string ProblemDetailsReference = ComponentSchemaPrefix + "ProblemDetails";
    private const string ProblemMediaType = "application/problem+json";
    private static readonly Regex KebabCaseSegment = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];
    private static readonly string[] ProtocolSurfaces = ["/connect", "/delivery", "/health", "/media", "/vfs"];
    private static readonly string[] ForbiddenQueryParameters = ["after", "platformId", "search"];
    private static readonly string[] OffsetRequestNames = ["offset", "page", "pageSize"];
    private static readonly string[] OffsetResponseProperties = ["offset", "pageSize"];
    private static readonly string[] RevisionSuffixes = ["revision", "revisions"];
    private static readonly string[] CursorProperties = ["hasMore", "nextAfter", "nextCursor"];
    private static readonly string[] PageProperties = ["hasNextPage", "items", "nextCursor"];
    private static readonly string[] SingleSubschemaKeywords = ["additionalProperties", "items", "not"];
    private static readonly string[] SubschemaArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems"];

    public static readonly IReadOnlyList<PolicyRule> All =
    [
        new("R1", "routes carry no literal admin segment",
            "Drop the /admin segment, because the host is the surface boundary (Resource Model).",
            AdminSegments),
        new("R2", "routes, parameter names and operationIds carry no platform token",
            "Say system instead of platform in the route, parameter names and operationId (Resource Model).",
            PlatformTokens),
        new("R3", "literal path segments are lowercase kebab-case",
            "Rename the literal segment to lowercase kebab-case (Resource Model).",
            NonKebabCaseSegments),
        new("R4", "DELETE declares no request body",
            "Address the deleted resource by its URL, or use POST /{collection}/batch-delete for a batch " +
            "(Method And Status Semantics).",
            DeleteRequestBodies),
        new("R5", "PUT and PATCH never document 202",
            "Return the updated representation with pendingJobId, or start background work with a POST action " +
            "(Async Operations And Jobs).",
            AcceptedReplacements),
        new("R6", "every 202 documents a Location header and a JSON job-reference body with jobId, and no ETag",
            "Return 202 only when the requested effect is background work, with the job-reference body and a " +
            "Location header for /api/jobs/{jobId} (Async Operations And Jobs).",
            IncompleteJobReferences),
        new("R7",
            "an operation documenting 412 declares a required If-Match header and documents 400 and 428, and an " +
            "operation declaring If-Match or documenting 428 documents 412",
            "Declare a required If-Match header with 400, 412 and 428 together; omit precondition metadata when the resource is " +
            "not concurrency-controlled (Idempotency And Concurrency).",
            InconsistentPreconditions),
        new("R8", "every 4xx and 5xx response except 401 and 403 documents application/problem+json ProblemDetails",
            "Document the status as application/problem+json with the ProblemDetails schema, e.g. " +
            ".ProducesProblem(status) (One Error Envelope).",
            UndocumentedProblems),
        new("R9",
            "every operation with a path parameter documents 404 unless a reviewed exemption records why an " +
            "unresolved id is not an error",
            "Document 404 (problem+json) for an unresolved path identity, or, when an unresolved id is legitimately " +
            "not an error (an idempotent DELETE, or a check that answers for any id), add a reviewed exemption with " +
            "a rationale (Resource Model, One Error Envelope).",
            MissingNotFound),
        new("R10", "no after, search or platformId query parameter, and every limit query parameter declares a maximum",
            "Use the cursor, query and systemKey parameters, and declare the limit maximum (Pagination).",
            NonStandardQueryParameters),
        new("R11", "every request revision field, parameter or header is a reviewed batch or snapshot binding",
            "Move the precondition to If-Match; only a genuine batch revision or snapshot binding goes in " +
            "ReviewedRequestRevisionFields, with a rationale (Idempotency And Concurrency).",
            RevisionInputs),
        new("R12",
            "every response schema with a cursor property is a PageOf* schema with exactly items, nextCursor and hasNextPage",
            "Return Page<T> from Romd.Contracts.Common, and move page-level extras to a sibling resource (Pagination).",
            NonStandardPages),
        new("R13",
            "no offset, page or pageSize query parameter or request-body property, and no response schema with an " +
            "offset or pageSize property",
            "Use keyset pagination with an opaque cursor (Pagination).",
            OffsetPaging),
        new("R14", "every operation with a request body documents 400 as application/problem+json ProblemDetails",
            "Declare the body's binding and validation failure, e.g. .ProducesProblems(StatusCodes.Status400BadRequest) " +
            "(One Error Envelope).",
            UndocumentedBodyFailures),
        new("R15", "every documented 201 declares a Location header",
            "Declare the create with .ProducesCreated<T>(), whose Location addresses the created member (Method And " +
            "Status Semantics).",
            CreatedWithoutLocation)
    ];

    public static bool IsApiRoute(string route) =>
        route == "/api" || route.StartsWith("/api/", StringComparison.Ordinal);

    public static IEnumerable<string> FindRoutesOutsideApi(string host, JsonDocument document) =>
        Members(document.RootElement, "paths")
            .Select(path => path.Name)
            .Where(route => !IsApiRoute(route) && !IsProtocolSurface(route))
            .Select(route => $"{host} {route}");

    /// <summary>
    ///     Shapes the rules do not interpret: a <c>$ref</c> anywhere under <c>paths</c> or
    ///     <c>components.schemas</c> that does not target an existing <c>#/components/schemas/</c> entry, a
    ///     <c>default</c> response, and a <c>$ref</c> parameter at path or operation level.
    /// </summary>
    public static IEnumerable<string> FindUnsupportedShapes(string host, JsonDocument document)
    {
        JsonElement schemas = ComponentSchemas(document);
        IReadOnlyList<JsonProperty> paths = [.. Members(document.RootElement, "paths")];
        return paths
            .SelectMany(path => References(path.Value, $"paths[{path.Name}]"))
            .Concat(References(schemas, "components.schemas"))
            .Where(reference => !IsComponentSchemaReference(reference.Target, schemas))
            .Select(reference =>
                $"{host} {reference.Location}: $ref '{reference.Target}' is not an existing components/schemas entry")
            .Concat(paths.SelectMany(path => HttpOperations(path.Value)
                .Where(operation => Members(operation.Value, "responses").Any(response => response.Name == "default"))
                .Select(operation => $"{host} paths[{path.Name}].{operation.Name}: documents a default response")))
            .Concat(paths.SelectMany(path => Elements(path.Value, "parameters")
                    .Select((parameter, index) => (Parameter: parameter, Location: $"paths[{path.Name}].parameters[{index}]"))
                    .Concat(HttpOperations(path.Value).SelectMany(operation => Elements(operation.Value, "parameters")
                        .Select((parameter, index) =>
                            (Parameter: parameter, Location: $"paths[{path.Name}].{operation.Name}.parameters[{index}]")))))
                .Where(parameter => parameter.Parameter.TryGetProperty("$ref", out _))
                .Select(parameter => $"{host} {parameter.Location}: parameter is a $ref"));
    }

    public static IReadOnlyList<string> FindAllowlistDrift(
        PolicyRule rule,
        IEnumerable<string> violations,
        IReadOnlyDictionary<string, string> allowlistHolders)
    {
        HashSet<string> detected = violations.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. detected.Where(key => !allowlistHolders.ContainsKey(key)).Order(StringComparer.Ordinal).Select(key =>
                $"{rule.Id} violation '{key}': {rule.Requirement}. Remedy: {rule.Remedy} See {PolicyDocument}; " +
                "migration allowlists only shrink."),
            .. allowlistHolders.Where(entry => !detected.Contains(entry.Key)).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{rule.Id} entry '{entry.Key}' in {entry.Value} no longer violates. Delete it.")
        ];
    }

    internal static JsonElement ComponentSchemas(JsonDocument document) =>
        document.RootElement.TryGetProperty("components", out JsonElement components)
        && components.TryGetProperty("schemas", out JsonElement schemas)
            ? schemas
            : default;

    internal static IEnumerable<JsonProperty> HttpOperations(JsonElement pathItem) =>
        pathItem.ValueKind == JsonValueKind.Object
            ? pathItem.EnumerateObject().Where(member => HttpMethods.Contains(member.Name, StringComparer.Ordinal))
            : [];

    internal static IEnumerable<JsonProperty> Members(JsonElement element, string objectName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(objectName, out JsonElement member)
        && member.ValueKind == JsonValueKind.Object
            ? member.EnumerateObject()
            : [];

    internal static IEnumerable<JsonElement> Elements(JsonElement element, string arrayName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(arrayName, out JsonElement array)
        && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static IEnumerable<string> AdminSegments(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => LiteralSegments(operation.Route).Contains("admin", StringComparer.OrdinalIgnoreCase))
            .Select(operation => operation.Key);

    private static IEnumerable<string> PlatformTokens(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Parameters.Select(Name)
                .Append(operation.Route)
                .Append(OperationId(operation))
                .Any(token => token.Contains("platform", StringComparison.OrdinalIgnoreCase)))
            .Select(operation => operation.Key);

    private static IEnumerable<string> NonKebabCaseSegments(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => LiteralSegments(operation.Route).Any(segment => !KebabCaseSegment.IsMatch(segment)))
            .Select(operation => operation.Key);

    private static IEnumerable<string> DeleteRequestBodies(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Method == "DELETE"
                && operation.Definition.TryGetProperty("requestBody", out _))
            .Select(operation => operation.Key);

    private static IEnumerable<string> AcceptedReplacements(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Method is "PUT" or "PATCH" && operation.Documents("202"))
            .Select(operation => operation.Key);

    private static IEnumerable<string> IncompleteJobReferences(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Responses.Any(response =>
                response.Name == "202" && !IsJobReference(response.Value, operation.Schemas)))
            .Select(operation => operation.Key);

    private static IEnumerable<string> InconsistentPreconditions(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Documents("412")
                ? !(operation.Documents("400") && operation.Documents("428")
                    && operation.Parameters.Any(parameter => IsIfMatch(parameter, true)))
                : operation.Documents("428") || operation.Parameters.Any(parameter => IsIfMatch(parameter, false)))
            .Select(operation => operation.Key);

    private static IEnumerable<string> UndocumentedProblems(IReadOnlyList<ApiOperation> operations) =>
        operations.SelectMany(operation => operation.Responses
            .Where(response => response.Name[0] is '4' or '5'
                && response.Name is not ("401" or "403")
                && !HasProblemDetails(response.Value))
            .Select(response => $"{operation.Key} {response.Name}"));

    private static IEnumerable<string> MissingNotFound(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => (operation.Route.Contains('{')
                    || operation.Parameters.Any(parameter => In(parameter) == "path"))
                && !operation.Documents("404"))
            .Select(operation => operation.Key);

    private static IEnumerable<string> NonStandardQueryParameters(IReadOnlyList<ApiOperation> operations) =>
        operations.SelectMany(operation => operation.Parameters
            .Where(parameter => In(parameter) == "query"
                && (ForbiddenQueryParameters.Contains(Name(parameter), StringComparer.OrdinalIgnoreCase)
                    || IsUnboundedLimit(parameter, operation.Schemas)))
            .Select(parameter => $"{operation.Key} ?{Name(parameter)}"));

    private static IEnumerable<string> RevisionInputs(IReadOnlyList<ApiOperation> operations) =>
        operations
            .GroupBy(operation => operation.Host)
            .SelectMany(host => ReachableSchemas(
                    host.SelectMany(operation =>
                        RequestBodySchemas(operation, $"{operation.Method} {operation.Route} requestBody")),
                    host.First().Schemas)
                .SelectMany(node => Members(node.Schema, "properties")
                    .Where(property => IsRevisionName(property.Name))
                    .Select(property => $"{host.Key} {node.Location}.{property.Name}")))
            .Concat(operations.SelectMany(operation => operation.Parameters
                .Where(parameter => In(parameter) is "query" or "header" && IsRevisionName(Name(parameter)))
                .Select(parameter => In(parameter) == "query"
                    ? $"{operation.Key} ?{Name(parameter)}"
                    : $"{operation.Key} header:{Name(parameter)}")))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> NonStandardPages(IReadOnlyList<ApiOperation> operations) =>
        operations
            .GroupBy(operation => operation.Host)
            .SelectMany(host => ReachableSchemas(host.SelectMany(ResponseSchemas), host.First().Schemas)
                .Where(node => HasProperty(node.Schema, CursorProperties) && !IsPage(node))
                .Select(node => $"{host.Key} {node.Location}"))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> OffsetPaging(IReadOnlyList<ApiOperation> operations) =>
        operations.SelectMany(operation => operation.Parameters
            .Where(parameter => In(parameter) == "query"
                && OffsetRequestNames.Contains(Name(parameter), StringComparer.OrdinalIgnoreCase))
            .Select(parameter => $"{operation.Key} ?{Name(parameter)}")
            .Concat(ReachableSchemas(
                    RequestBodySchemas(operation, "requestBody"),
                    operation.Schemas,
                    locateComponentsByName: false)
                .SelectMany(node => Members(node.Schema, "properties")
                    .Where(property => OffsetRequestNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(property => $"{operation.Key} {node.Location}.{property.Name}")))
            .Concat(operation.Responses
                .Where(response => ReachableSchemas(
                        MediaSchemas(response.Value).Select(schema => new SchemaNode(response.Name, false, schema)),
                        operation.Schemas)
                    .Any(node => HasProperty(node.Schema, OffsetResponseProperties)))
                .Select(response => $"{operation.Key} {response.Name}")))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> UndocumentedBodyFailures(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Definition.TryGetProperty("requestBody", out _)
                && !operation.Responses.Any(response => response.Name == "400" && HasProblemDetails(response.Value)))
            .Select(operation => operation.Key);

    private static IEnumerable<string> CreatedWithoutLocation(IReadOnlyList<ApiOperation> operations) =>
        operations
            .Where(operation => operation.Responses.Any(response => response.Name == "201" && !DeclaresLocation(response.Value)))
            .Select(operation => operation.Key);

    private static bool DeclaresLocation(JsonElement response) =>
        Members(response, "headers").Any(header => header.Name.Equals("Location", StringComparison.OrdinalIgnoreCase));

    private static bool IsJobReference(JsonElement response, JsonElement schemas) =>
        DeclaresLocation(response)
        && !Members(response, "headers").Any(header => header.Name.Equals("ETag", StringComparison.OrdinalIgnoreCase))
        && MediaSchemas(response, "application/json").Any(schema =>
            Members(ResolveSchema(schema, schemas), "properties").Any(property => property.Name == "jobId"));

    private static bool IsIfMatch(JsonElement parameter, bool required) =>
        In(parameter) == "header"
        && Name(parameter).Equals("If-Match", StringComparison.OrdinalIgnoreCase)
        && (!required
            || (parameter.TryGetProperty("required", out JsonElement isRequired)
                && isRequired.ValueKind == JsonValueKind.True));

    private static bool HasProblemDetails(JsonElement response) =>
        MediaSchemas(response, ProblemMediaType).Any(schema => schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out JsonElement reference)
            && reference.ToString() == ProblemDetailsReference);

    private static bool IsUnboundedLimit(JsonElement parameter, JsonElement schemas) =>
        Name(parameter).Equals("limit", StringComparison.OrdinalIgnoreCase)
        && !(parameter.TryGetProperty("schema", out JsonElement schema)
            && ResolveSchema(schema, schemas) is { ValueKind: JsonValueKind.Object } resolved
            && resolved.TryGetProperty("maximum", out _));

    private static bool IsRevisionName(string name) =>
        RevisionSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static bool HasProperty(JsonElement schema, string[] names) =>
        Members(schema, "properties").Any(property => names.Contains(property.Name, StringComparer.OrdinalIgnoreCase));

    private static bool IsPage(SchemaNode node) =>
        node.IsComponent
        && node.Location.StartsWith("PageOf", StringComparison.Ordinal)
        && Members(node.Schema, "properties").Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(PageProperties, StringComparer.Ordinal);

    private static IEnumerable<SchemaNode> RequestBodySchemas(ApiOperation operation, string rootLabel) =>
        MediaSchemas(RequestBody(operation)).Select(schema => new SchemaNode(rootLabel, false, schema));

    private static JsonElement RequestBody(ApiOperation operation) =>
        operation.Definition.TryGetProperty("requestBody", out JsonElement requestBody) ? requestBody : default;

    private static IEnumerable<SchemaNode> ResponseSchemas(ApiOperation operation) =>
        operation.Responses.SelectMany(response => MediaSchemas(response.Value).Select(schema =>
            new SchemaNode($"{operation.Method} {operation.Route} {response.Name}", false, schema)));

    private static IEnumerable<JsonElement> MediaSchemas(JsonElement container, string? mediaType = null) =>
        Members(container, "content")
            .Where(media => (mediaType is null || media.Name == mediaType)
                && media.Value.ValueKind == JsonValueKind.Object
                && media.Value.TryGetProperty("schema", out _))
            .Select(media => media.Value.GetProperty("schema"));

    /// <summary>
    ///     Every schema reachable from the roots through component-schema <c>$ref</c>s, properties, items,
    ///     additional properties, <c>not</c> and composition. An inline schema is located by its owner and
    ///     property path. A component schema is located by its name, or, without
    ///     <paramref name="locateComponentsByName" />, by the path that first reached it. A <c>$ref</c> that is
    ///     not an existing component schema is skipped; <see cref="FindUnsupportedShapes" /> reports it.
    /// </summary>
    private static IReadOnlyList<SchemaNode> ReachableSchemas(
        IEnumerable<SchemaNode> roots,
        JsonElement schemas,
        bool locateComponentsByName = true)
    {
        var visitedComponents = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<SchemaNode>(roots);
        var reached = new List<SchemaNode>();
        while (pending.TryPop(out SchemaNode? node))
        {
            if (node.Schema.ValueKind != JsonValueKind.Object)
                continue;
            reached.Add(node);
            if (node.Schema.TryGetProperty("$ref", out JsonElement reference)
                && TryGetComponentSchema(reference.ToString(), schemas, out string name, out JsonElement component)
                && visitedComponents.Add(name))
                pending.Push(new SchemaNode(locateComponentsByName ? name : node.Location, true, component));

            foreach (JsonProperty property in Members(node.Schema, "properties"))
                pending.Push(new SchemaNode($"{node.Location}.{property.Name}", false, property.Value));
            foreach (JsonElement subschema in Subschemas(node.Schema))
                pending.Push(node with { IsComponent = false, Schema = subschema });
        }

        return reached;
    }

    private static bool TryGetComponentSchema(
        string target,
        JsonElement schemas,
        out string name,
        out JsonElement component)
    {
        name = target.StartsWith(ComponentSchemaPrefix, StringComparison.Ordinal)
            ? target[ComponentSchemaPrefix.Length..]
            : string.Empty;
        component = default;
        return name.Length > 0
            && schemas.ValueKind == JsonValueKind.Object
            && schemas.TryGetProperty(name, out component);
    }

    private static bool IsComponentSchemaReference(string target, JsonElement schemas) =>
        TryGetComponentSchema(target, schemas, out _, out _);

    private static JsonElement ResolveSchema(JsonElement schema, JsonElement schemas) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("$ref", out JsonElement reference)
        && TryGetComponentSchema(reference.ToString(), schemas, out _, out JsonElement component)
            ? component
            : schema;

    private static IEnumerable<(string Location, string Target)> References(JsonElement element, string location) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().SelectMany(property =>
                property.Name == "$ref"
                    ? Enumerable.Repeat((location, property.Value.ToString()), 1)
                    : References(property.Value, $"{location}.{property.Name}")),
            JsonValueKind.Array => element.EnumerateArray()
                .SelectMany((item, index) => References(item, $"{location}[{index}]")),
            _ => []
        };

    private static IEnumerable<JsonElement> Subschemas(JsonElement schema) =>
        SingleSubschemaKeywords
            .Where(keyword => schema.TryGetProperty(keyword, out _))
            .Select(keyword => schema.GetProperty(keyword))
            .Concat(SubschemaArrayKeywords
                .Where(keyword => schema.TryGetProperty(keyword, out JsonElement array)
                    && array.ValueKind == JsonValueKind.Array)
                .SelectMany(keyword => schema.GetProperty(keyword).EnumerateArray()));

    private static bool IsProtocolSurface(string route) =>
        ProtocolSurfaces.Any(surface => route == surface || route.StartsWith(surface + "/", StringComparison.Ordinal))
        || (route.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["artwork", var asset, ..] && IsParameter(asset));

    private static IEnumerable<string> LiteralSegments(string route) =>
        route.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => !IsParameter(segment));

    private static bool IsParameter(string segment) => segment.StartsWith('{') && segment.EndsWith('}');

    private static string OperationId(ApiOperation operation) => StringMember(operation.Definition, "operationId");

    private static string Name(JsonElement parameter) => StringMember(parameter, "name");

    private static string In(JsonElement parameter) => StringMember(parameter, "in");

    private static string StringMember(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    private sealed record SchemaNode(string Location, bool IsComponent, JsonElement Schema);
}
