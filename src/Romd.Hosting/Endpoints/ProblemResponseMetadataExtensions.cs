using Microsoft.AspNetCore.Mvc;

namespace Romd.Host.Endpoints;

/// <summary>
///     The one way endpoints declare their error responses
///     (docs/decisions/api-contract-policy.md, "Guardrails"): each status is published with the
///     ProblemDetails schema and <c>application/problem+json</c>. The bodies themselves come only
///     from <see cref="ProblemResults" /> and <see cref="ErrorOrResultExtensions.ToProblem" />
///     (docs/decisions/api-contract-policy.md, "One Error Envelope"). 401 and 403 written by the
///     authentication and authorization middleware are not declared here.
/// </summary>
public static class ProblemResponseMetadataExtensions
{
    private const string ProblemMediaType = "application/problem+json";

    public static RouteHandlerBuilder ProducesProblems(this RouteHandlerBuilder builder, params int[] statusCodes) =>
        statusCodes.Aggregate(builder, (current, statusCode) => current.WithMetadata(ProblemResponse(statusCode)));

    /// <summary>
    ///     The declaration of one error status, for conventions that decide per endpoint which statuses apply.
    /// </summary>
    public static ProducesResponseTypeMetadata ProblemResponse(int statusCode) => statusCode is >= 400 and <= 599
        ? new ProducesResponseTypeMetadata(statusCode, typeof(ProblemDetails), [ProblemMediaType])
        : throw new ArgumentOutOfRangeException(
            nameof(statusCode),
            statusCode,
            "Only 4xx and 5xx statuses are error responses.");
}
