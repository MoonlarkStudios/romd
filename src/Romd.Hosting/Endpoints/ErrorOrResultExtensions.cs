using ErrorOr;
using Romd.Application.Common.Errors;
using Romd.Application.Common.Validation;

namespace Romd.Host.Endpoints;

public static class ErrorOrResultExtensions
{
    /// <summary>
    ///     The envelope for a failed stage. Its status and errorCode come from the first error, or
    ///     <paramref name="errorCode" /> names the stage as a whole; <c>errors</c> preserves every validation
    ///     failure, keyed by the input it names (<see cref="FieldErrors" />) or else by its code.
    /// </summary>
    public static IResult ToProblem(this IReadOnlyList<Error> errors, string? errorCode = null)
    {
        if (errors.Count == 0)
        {
            return ProblemResults.Problem(
                StatusCodes.Status500InternalServerError,
                ProblemCodes.GeneralUnexpected,
                "An unexpected error occurred.");
        }

        var first = errors[0];

        return ProblemResults.Problem(
            ToStatusCode(first.Type),
            errorCode ?? (string.IsNullOrWhiteSpace(first.Code) ? ProblemCodes.GeneralUnexpected : first.Code),
            first.Description,
            CollectValidationFailures(errors),
            RetryAfterMetadata.Of(first));
    }

    // A validation error that names its input (FieldErrors) is keyed by that wire name; one that names none
    // is keyed by its stable code (docs/decisions/api-contract-policy.md, "One Error Envelope").
    private static Dictionary<string, string[]>? CollectValidationFailures(IReadOnlyList<Error> errors)
    {
        var failures = errors
            .Where(error => error.Type == ErrorType.Validation)
            .GroupBy(error => FieldErrors.FieldOf(error) ?? error.Code)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Description).ToArray());

        return failures.Count > 0 ? failures : null;
    }

    // Preconditions (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency") and upstream provider
    // failures (502 for an invalid response, 503 when unavailable) are ErrorOr custom types carrying their HTTP status.
    private static int ToStatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        (ErrorType)StatusCodes.Status412PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        (ErrorType)StatusCodes.Status428PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        (ErrorType)StatusCodes.Status502BadGateway => StatusCodes.Status502BadGateway,
        (ErrorType)StatusCodes.Status503ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
}
