using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Romd.Host.Endpoints;

/// <summary>
///     Single production path for endpoint-produced non-2xx bodies. Every error response is an
///     RFC 9457 ProblemDetails carrying the ROMD envelope extensions
///     (docs/decisions/admin-api-contract-policy.md, "One Error Envelope"):
///     <c>errorCode</c> (stable machine-readable code), <c>errors</c> (field-to-messages map,
///     present only when validation failures exist, preserving all failures), and <c>traceId</c>
///     (<c>Activity.Current?.Id ?? HttpContext.TraceIdentifier</c>, stamped at execution time).
/// </summary>
public static class ProblemResults
{
    public const string ErrorCodeExtensionName = "errorCode";
    public const string ErrorsExtensionName = "errors";
    public const string TraceIdExtensionName = "traceId";

    /// <summary>Builds an envelope-conforming problem result for the given status code.</summary>
    public static IResult Problem(
        int statusCode,
        string errorCode,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Detail = detail
        };

        problemDetails.Extensions[ErrorCodeExtensionName] = errorCode;
        if (validationErrors is { Count: > 0 })
        {
            problemDetails.Extensions[ErrorsExtensionName] = validationErrors;
        }

        return new EnvelopeProblemResult(problemDetails);
    }

    /// <summary>Builds a 400 envelope preserving every supplied validation failure.</summary>
    public static IResult ValidationProblem(
        string errorCode,
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null) =>
        Problem(
            StatusCodes.Status400BadRequest,
            errorCode,
            detail ?? "One or more request values are invalid.",
            errors);

    /// <summary>Builds a 400 envelope for a single invalid field.</summary>
    public static IResult ValidationProblem(
        string errorCode,
        string field,
        string message,
        string? detail = null) =>
        ValidationProblem(
            errorCode,
            new Dictionary<string, string[]> { [field] = [message] },
            detail ?? message);

    /// <summary>
    ///     Wraps <see cref="ProblemHttpResult" /> so the trace identifier is resolved from the
    ///     executing request, keeping construction sites free of HttpContext plumbing.
    /// </summary>
    private sealed class EnvelopeProblemResult(ProblemDetails problemDetails)
        : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<ProblemDetails>
    {
        private readonly ProblemHttpResult _inner = TypedResults.Problem(problemDetails);

        public int? StatusCode => _inner.StatusCode;

        object? IValueHttpResult.Value => _inner.ProblemDetails;

        ProblemDetails? IValueHttpResult<ProblemDetails>.Value => _inner.ProblemDetails;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            _inner.ProblemDetails.Extensions.TryAdd(
                TraceIdExtensionName,
                Activity.Current?.Id ?? httpContext.TraceIdentifier);
            return _inner.ExecuteAsync(httpContext);
        }
    }
}
