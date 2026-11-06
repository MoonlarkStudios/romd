using ErrorOr;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Validation;

namespace Romd.Host.Endpoints;

/// <summary>
///     The one helper for ETag emission and precondition checks (docs/decisions/api-contract-policy.md,
///     "Idempotency And Concurrency" and "Guardrails"). The validator is the representation's revision in exactly
///     one pair of double quotes, never weak. Reads honor <c>If-None-Match</c> with weak comparison (RFC 9110
///     section 13.1.2); writes require <c>If-Match</c>, which compares strongly, and pass the echoed validators to
///     the handler, which compares them with the current revision atomically and answers a stale tag with 412.
/// </summary>
public static class Preconditions
{
    private const string RevalidateCacheControl = "private, no-cache";

    private static readonly int[] PreconditionStatuses =
        [StatusCodes.Status400BadRequest, StatusCodes.Status412PreconditionFailed, StatusCodes.Status428PreconditionRequired];


    /// <summary>
    ///     Answers a GET of a resource whose revision versions its whole representation: 304 when
    ///     <c>If-None-Match</c> holds <c>*</c> or a tag that weakly matches the current validator, so <c>W/</c>
    ///     of it matches too, otherwise 200 with the representation.
    /// </summary>
    public static IResult ConditionalRead<T>(HttpContext context, T representation, string revision)
    {
        var current = new EntityTagHeaderValue(ETag(revision));
        context.Response.Headers.ETag = current.ToString();
        context.Response.Headers.CacheControl = RevalidateCacheControl;
        return EntityTagHeaderValue.TryParseList(context.Request.Headers.IfNoneMatch, out var tags)
               && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Ok(representation);
    }

    /// <summary>Stamps a successful response with the resource's current validator.</summary>
    public static IResult WithETag(this IResult result, HttpContext context, string revision)
    {
        context.Response.Headers.ETag = ETag(revision);
        return result;
    }

    /// <summary>
    ///     Reads <c>If-Match</c> for a write. An absent or empty header, or <c>*</c>, which cannot prevent a lost
    ///     update, is 428 <c>{area}.PreconditionRequired</c>. Malformed syntax or a nonempty list without tags is
    ///     a 400 attributed to <c>If-Match</c>. Otherwise the strong validators go to the handler; a list holding
    ///     only weak tags gives a precondition nothing satisfies, so the handler answers 412.
    /// </summary>
    public static ErrorOr<RevisionPrecondition> IfMatch(HttpContext context, string area)
    {
        var header = context.Request.Headers.IfMatch;
        if (string.IsNullOrWhiteSpace(header.ToString()))
            return Required(area);
        if (!EntityTagHeaderValue.TryParseStrictList(header, out var tags) || tags.Count == 0)
            return InvalidIfMatch();
        if (tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any)))
            return tags.Count == 1 && header.ToString().Trim() == "*" ? Required(area) : InvalidIfMatch();
        return new RevisionPrecondition(tags
            .Where(tag => !tag.IsWeak)
            .Select(tag => HeaderUtilities.RemoveQuotes(tag.Tag).ToString()));
    }

    /// <summary>A GET of a resource whose revision versions its whole representation: the <c>ETag</c> and 304.</summary>
    public static RouteHandlerBuilder ConditionalRead(this RouteHandlerBuilder builder) =>
        builder.Declare(Use.ConditionalRead);

    /// <summary>
    ///     A GET whose validator guards writes but does not version the whole representation: the <c>ETag</c>
    ///     response header only.
    /// </summary>
    public static RouteHandlerBuilder ETagOnlyRead(this RouteHandlerBuilder builder) =>
        builder.Declare(Use.ETagOnlyRead);

    /// <summary>A write: a required <c>If-Match</c> header, 400, 412 and 428 problems, and the success response's <c>ETag</c>.</summary>
    public static RouteHandlerBuilder ConditionalWrite(this RouteHandlerBuilder builder) =>
        builder.Declare(Use.ConditionalWrite);

    /// <summary>
    ///     A write that removes the resource or answers without its representation: a required <c>If-Match</c>
    ///     header and 400, 412 and 428 problems, but no <c>ETag</c>.
    /// </summary>
    public static RouteHandlerBuilder ConditionalWriteWithoutETag(this RouteHandlerBuilder builder) =>
        builder.Declare(Use.ConditionalWriteWithoutETag);

    /// <summary>A create, which has no validator to match: only the created resource's <c>ETag</c>.</summary>
    public static RouteHandlerBuilder ConditionalCreate(this RouteHandlerBuilder builder) =>
        builder.Declare(Use.ConditionalCreate);

    private enum Use { ConditionalRead, ETagOnlyRead, ConditionalWrite, ConditionalWriteWithoutETag, ConditionalCreate }

    /// <summary>
    ///     Declares one use. A read use on a write method, or the reverse, fails when the endpoint is built.
    /// </summary>
    private static RouteHandlerBuilder Declare(this RouteHandlerBuilder builder, Use use)
    {
        bool read = use is Use.ConditionalRead or Use.ETagOnlyRead;
        bool preconditioned = use is Use.ConditionalWrite or Use.ConditionalWriteWithoutETag;
        builder.Add(endpoint =>
        {
            if (IsRead(endpoint.Metadata) != read)
                throw new InvalidOperationException(
                    $"{use} cannot describe '{endpoint.DisplayName}': reads are GETs and writes are not.");
            if (use is Use.ConditionalRead)
                endpoint.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status304NotModified, typeof(void)));
        });
        if (preconditioned)
            ((IEndpointConventionBuilder)builder).Finally(endpoint =>
            {
                // A write may also declare 400 for its body or query. Normalize after its conventions so
                // the shared malformed-header response and those failures publish one problem schema.
                for (int index = endpoint.Metadata.Count - 1; index >= 0; index--)
                    if (endpoint.Metadata[index] is IProducesResponseTypeMetadata response
                        && PreconditionStatuses.Contains(response.StatusCode))
                        endpoint.Metadata.RemoveAt(index);
                foreach (int status in PreconditionStatuses)
                    endpoint.Metadata.Add(ProblemResponseMetadataExtensions.ProblemResponse(status));
            });
        return builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (preconditioned)
                DeclareIfMatch(operation);
            if (use is not Use.ConditionalWriteWithoutETag)
                DeclareETag(operation);
            return Task.CompletedTask;
        });
    }

    /// <summary>The ETag value for a revision.</summary>
    private static string ETag(string revision) => $"\"{revision}\"";

    private static Error Required(string area) =>
        Error.Custom(StatusCodes.Status428PreconditionRequired, $"{area}.PreconditionRequired",
            "Send the resource ETag in If-Match.");

    private static Error InvalidIfMatch() =>
        FieldErrors.Validation(ProblemCodes.RequestInvalidParameter,
            "If-Match must contain valid entity-tag syntax.", HeaderNames.IfMatch);

    private static bool IsRead(IEnumerable<object> metadata) =>
        metadata.OfType<IHttpMethodMetadata>().SelectMany(method => method.HttpMethods)
            .Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase);

    private static void DeclareIfMatch(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderNames.IfMatch,
            In = ParameterLocation.Header,
            Required = true,
            Description = "One or more resource ETags the client last read. Malformed syntax is 400; no strong match is 412; missing, empty or * is 428.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }

    private static void DeclareETag(OpenApiOperation operation)
    {
        foreach (var response in operation.Responses?.Where(response => response.Key.StartsWith('2'))
                     .Select(response => response.Value).OfType<OpenApiResponse>() ?? [])
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
            response.Headers[HeaderNames.ETag] = new OpenApiHeader
            {
                Description = "The resource's current validator: its revision in double quotes.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            };
        }
    }
}
